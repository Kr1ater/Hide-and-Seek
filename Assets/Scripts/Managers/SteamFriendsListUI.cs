using System.Collections.Generic;
using System.Text;
using Steamworks;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

// Внутриигровой список друзей: показывает тех, кто прямо сейчас запустил эту же игру,
// с их аватарками из Steam, и позволяет подключиться в один клик.
//
// Работает независимо от оверлея Steam, поэтому это основной способ подключения
// на macOS (где оверлей сломан). На Windows работает наравне с приглашениями через оверлей.
//
// Прокручиваемый список строится кодом - собирать иерархию ScrollRect в редакторе не нужно.
public class SteamFriendsListUI : MonoBehaviour
{
    [Header("Панель, внутри которой построить прокручиваемый список")]
    [SerializeField] private RectTransform friendListContainer;

    [Header("Ссылки")]
    [SerializeField] private TextMeshProUGUI statusText;

    [Header("Настройки")]
    [SerializeField] private float refreshIntervalSeconds = 2f;
    [SerializeField] private float entryHeight = 56f;
    [SerializeField] private float avatarSize = 40f;

    private static readonly Color HostingColor = new Color(0.24f, 0.48f, 0.29f, 0.95f);
    private static readonly Color HostingHoverColor = new Color(0.30f, 0.60f, 0.36f, 1f);
    private static readonly Color IdleColor = new Color(1f, 1f, 1f, 0.10f);
    private static readonly Color IdleHoverColor = new Color(1f, 1f, 1f, 0.20f);

    private readonly List<GameObject> _spawnedEntries = new List<GameObject>();
    private readonly Dictionary<ulong, RawImage> _avatarSlots = new Dictionary<ulong, RawImage>();

    // Сюда складываются карточки. Создаётся кодом внутри friendListContainer.
    private RectTransform _content;

    private float _refreshTimer;
    private string _lastListSignature = "";

    private void Awake()
    {
        if (friendListContainer == null)
        {
            Debug.LogError($"В {nameof(SteamFriendsListUI)} не назначен {nameof(friendListContainer)} - список друзей показываться не будет.");
            return;
        }

        // Контейнер мы скрываем при подключении. Если бы им оказался объект самого скрипта
        // (или его родитель), скрипт выключил бы сам себя и больше никогда не включился.
        if (friendListContainer == transform || transform.IsChildOf(friendListContainer))
        {
            Debug.LogError($"{nameof(friendListContainer)} не должен быть тем же объектом, на котором висит {nameof(SteamFriendsListUI)}, или его родителем. Повесьте скрипт на отдельный объект.");
            friendListContainer = null;
            return;
        }

        BuildScrollView();
    }

    // Собираем стандартную связку ScrollRect: Viewport с обрезкой + Content, который
    // растёт вниз по количеству карточек. Без этого длинный список уезжал вверх за экран.
    private void BuildScrollView()
    {
        // Раскладка должна быть на Content, а не на самой панели
        var strayLayout = friendListContainer.GetComponent<VerticalLayoutGroup>();
        if (strayLayout != null) Destroy(strayLayout);

        var viewportObject = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
        var viewport = (RectTransform)viewportObject.transform;
        viewport.SetParent(friendListContainer, false);
        viewport.anchorMin = Vector2.zero;
        viewport.anchorMax = Vector2.one;
        viewport.offsetMin = Vector2.zero;
        viewport.offsetMax = Vector2.zero;

        var contentObject = new GameObject("Content", typeof(RectTransform));
        _content = (RectTransform)contentObject.transform;
        _content.SetParent(viewport, false);
        // Привязка к верхнему краю: список наполняется сверху вниз
        _content.anchorMin = new Vector2(0f, 1f);
        _content.anchorMax = new Vector2(1f, 1f);
        _content.pivot = new Vector2(0.5f, 1f);
        _content.sizeDelta = new Vector2(0f, 0f);

        var layout = contentObject.AddComponent<VerticalLayoutGroup>();
        layout.spacing = 8f;
        layout.padding = new RectOffset(8, 8, 8, 8);
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = true;

        // Именно это заставляет Content расти под содержимое, а прокрутку - работать
        var sizeFitter = contentObject.AddComponent<ContentSizeFitter>();
        sizeFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        sizeFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

        var scrollRect = friendListContainer.GetComponent<ScrollRect>();
        if (scrollRect == null) scrollRect = friendListContainer.gameObject.AddComponent<ScrollRect>();

        scrollRect.viewport = viewport;
        scrollRect.content = _content;
        scrollRect.horizontal = false;
        scrollRect.vertical = true;
        scrollRect.movementType = ScrollRect.MovementType.Elastic;
        scrollRect.scrollSensitivity = 25f;
    }

    private void Update()
    {
        if (friendListContainer == null) return;

        // Пока мы уже в сети, список не нужен - прячем его
        bool isConnected = NetworkManager.Singleton != null
                           && (NetworkManager.Singleton.IsClient || NetworkManager.Singleton.IsServer);

        if (friendListContainer.gameObject.activeSelf == isConnected)
        {
            friendListContainer.gameObject.SetActive(!isConnected);
        }

        if (isConnected || !SteamManager.IsSteamInitialized || SteamManager.Instance == null) return;

        // Аватарки Steam догружает асинхронно, поэтому подставляем их по мере готовности
        FillPendingAvatars();

        _refreshTimer -= Time.deltaTime;
        if (_refreshTimer > 0f) return;

        _refreshTimer = refreshIntervalSeconds;
        RefreshFriendList();
    }

    private void RefreshFriendList()
    {
        List<SteamFriendInfo> friends = SteamManager.Instance.GetFriendsInGame();

        // Пересобираем карточки только если состав списка реально изменился:
        // иначе каждые 2 секунды был бы мусор в памяти и моргание кнопок.
        string signature = BuildSignature(friends);
        if (signature == _lastListSignature) return;

        _lastListSignature = signature;

        ClearEntries();

        foreach (SteamFriendInfo friend in friends)
        {
            CreateFriendEntry(friend);
        }

        if (statusText != null)
        {
            statusText.SetText(friends.Count == 0
                ? "Друзей в игре нет. Пусть друг запустит игру и нажмёт Host."
                : $"Друзей в игре: {friends.Count}");
        }
    }

    private static string BuildSignature(List<SteamFriendInfo> friends)
    {
        var builder = new StringBuilder();

        foreach (SteamFriendInfo friend in friends)
        {
            builder.Append(friend.SteamId.m_SteamID).Append(friend.IsHosting ? ":h;" : ":i;");
        }

        return builder.ToString();
    }

    private void CreateFriendEntry(SteamFriendInfo friend)
    {
        var entry = new GameObject($"Friend_{friend.SteamId.m_SteamID}", typeof(RectTransform), typeof(Image), typeof(Button));
        entry.transform.SetParent(_content, false);

        // Фиксированная высота карточки: по ней VerticalLayoutGroup считает размер Content
        var layoutElement = entry.AddComponent<LayoutElement>();
        layoutElement.preferredHeight = entryHeight;
        layoutElement.minHeight = entryHeight;

        // Белый фон: сам цвет карточки задаётся через ColorBlock кнопки, иначе Unity
        // перемножил бы цвет картинки на цвет состояния и оттенки получились бы грязными.
        var background = entry.GetComponent<Image>();
        background.color = Color.white;

        // Подсветка при наведении, чтобы карточка ощущалась кликабельной
        var button = entry.GetComponent<Button>();
        var colors = button.colors;
        colors.normalColor = friend.IsHosting ? HostingColor : IdleColor;
        colors.highlightedColor = friend.IsHosting ? HostingHoverColor : IdleHoverColor;
        colors.pressedColor = colors.highlightedColor;
        colors.selectedColor = colors.normalColor;
        button.colors = colors;
        button.targetGraphic = background;
        button.onClick.AddListener(() => ConnectToFriend(friend.SteamId));

        CreateAvatar(entry.transform, friend);
        CreateLabels(entry.transform, friend);

        _spawnedEntries.Add(entry);
    }

    private void CreateAvatar(Transform parent, SteamFriendInfo friend)
    {
        var avatarObject = new GameObject("Avatar", typeof(RectTransform), typeof(RawImage));
        avatarObject.transform.SetParent(parent, false);

        var avatarRect = (RectTransform)avatarObject.transform;
        avatarRect.anchorMin = new Vector2(0f, 0.5f);
        avatarRect.anchorMax = new Vector2(0f, 0.5f);
        avatarRect.pivot = new Vector2(0f, 0.5f);
        avatarRect.anchoredPosition = new Vector2(10f, 0f);
        avatarRect.sizeDelta = new Vector2(avatarSize, avatarSize);

        var rawImage = avatarObject.GetComponent<RawImage>();
        Texture2D avatar = SteamManager.Instance.GetFriendAvatar(friend.SteamId);

        if (avatar != null)
        {
            rawImage.texture = avatar;
        }
        else
        {
            // Заглушка, пока Steam не догрузил картинку
            rawImage.color = new Color(1f, 1f, 1f, 0.25f);
            _avatarSlots[friend.SteamId.m_SteamID] = rawImage;
        }
    }

    private void CreateLabels(Transform parent, SteamFriendInfo friend)
    {
        float textLeft = avatarSize + 20f;

        // Имя занимает верхнюю часть карточки, статус - нижнюю.
        // Раньше оба текста растягивались на всю высоту и наползали друг на друга.
        var nameText = CreateLabel(parent, "Name", textLeft, new Vector2(0f, 0.45f), new Vector2(1f, 1f));
        nameText.SetText(friend.Name);
        nameText.fontSize = 18f;
        nameText.fontStyle = FontStyles.Bold;
        nameText.alignment = TextAlignmentOptions.BottomLeft;

        var statusLabel = CreateLabel(parent, "Status", textLeft, new Vector2(0f, 0f), new Vector2(1f, 0.45f));
        statusLabel.SetText(friend.IsHosting ? "Хостит игру - нажмите, чтобы подключиться" : "В игре");
        statusLabel.fontSize = 13f;
        statusLabel.alignment = TextAlignmentOptions.TopLeft;
        statusLabel.color = new Color(1f, 1f, 1f, 0.7f);
    }

    private static TextMeshProUGUI CreateLabel(Transform parent, string name, float leftPadding, Vector2 anchorMin, Vector2 anchorMax)
    {
        var labelObject = new GameObject(name, typeof(RectTransform));
        labelObject.transform.SetParent(parent, false);

        var labelRect = (RectTransform)labelObject.transform;
        labelRect.anchorMin = anchorMin;
        labelRect.anchorMax = anchorMax;
        labelRect.offsetMin = new Vector2(leftPadding, 0f);
        labelRect.offsetMax = new Vector2(-10f, 0f);

        var text = labelObject.AddComponent<TextMeshProUGUI>();
        // Длинное имя не должно переноситься и распирать карточку по высоте
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Ellipsis;

        return text;
    }

    // Steam присылает аватарки асинхронно: подставляем их в уже созданные карточки
    private void FillPendingAvatars()
    {
        if (_avatarSlots.Count == 0) return;

        var filled = new List<ulong>();

        foreach (KeyValuePair<ulong, RawImage> slot in _avatarSlots)
        {
            if (slot.Value == null)
            {
                filled.Add(slot.Key);
                continue;
            }

            Texture2D avatar = SteamManager.Instance.GetFriendAvatar(new CSteamID(slot.Key));
            if (avatar == null) continue;

            slot.Value.texture = avatar;
            slot.Value.color = Color.white;
            filled.Add(slot.Key);
        }

        foreach (ulong steamId in filled)
        {
            _avatarSlots.Remove(steamId);
        }
    }

    private void ConnectToFriend(CSteamID hostSteamId)
    {
        if (NetworkManager.Singleton == null)
        {
            Debug.LogError("NetworkManager не найден - подключиться невозможно.");
            return;
        }

        // Хост покажет наше имя в окне подтверждения
        NetworkManager.Singleton.NetworkConfig.ConnectionData = SteamManager.Instance.BuildConnectionPayload();

        SteamTransportLocator.TryConnectTo(hostSteamId.m_SteamID);
    }

    private void ClearEntries()
    {
        foreach (GameObject entry in _spawnedEntries)
        {
            Destroy(entry);
        }

        _spawnedEntries.Clear();
        _avatarSlots.Clear();
    }
}
