using System.Collections.Generic;
using Steamworks;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

// Хост решает, пускать ли игрока: при попытке подключения показывается окно
// "<имя> хочет присоединиться. Принять / Отклонить".
//
// Держится на Netcode Connection Approval: сервер помечает решение как Pending
// и держит подключение открытым, пока человек не нажмёт кнопку.
//
// ВАЖНО: в NetworkManager должна быть включена галочка Connection Approval.
// Если она включена, а обработчик не назначен, Netcode молча подвешивает
// подключающегося - ни подтверждения, ни отказа. Поэтому обработчик здесь
// ставится всегда, а при уничтожении объекта подменяется на авто-приём.
public class JoinRequestApprovalUI : MonoBehaviour
{
    [Header("Canvas, в котором показать окно (пусто = найти в сцене)")]
    [SerializeField] private Canvas uiCanvas;

    [Header("Настройки")]
    [Tooltip("Через сколько секунд без ответа запрос отклоняется сам. 0 - ждать бесконечно.")]
    [SerializeField] private float autoDeclineSeconds = 30f;

    private readonly Queue<PendingRequest> _pendingRequests = new Queue<PendingRequest>();

    private PendingRequest _currentRequest;
    private GameObject _dialogRoot;
    private TextMeshProUGUI _dialogText;
    private float _timeLeft;

    private class PendingRequest
    {
        public NetworkManager.ConnectionApprovalResponse Response;
        public string DisplayName;
    }

    private bool _callbackRegistered;

    // Регистрируем в Start, а не в Awake: NetworkManager выставляет свой Singleton
    // в собственном OnEnable, и порядок между объектами Unity не гарантирует.
    // Если в Awake Singleton ещё null, обработчик не встанет, и при включённом
    // Connection Approval подключающийся зависнет навсегда.
    private void Start()
    {
        TryRegisterCallback();
    }

    private void TryRegisterCallback()
    {
        if (_callbackRegistered || NetworkManager.Singleton == null) return;

        NetworkManager.Singleton.ConnectionApprovalCallback = HandleConnectionApproval;
        _callbackRegistered = true;
    }

    private void OnDestroy()
    {
        if (NetworkManager.Singleton == null) return;

        // Не обнуляем, а подменяем: пустой обработчик при включённом Connection Approval
        // означал бы, что подключающийся навсегда зависает в ожидании.
        NetworkManager.Singleton.ConnectionApprovalCallback = ApproveSilently;
    }

    private static void ApproveSilently(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
    {
        response.Approved = true;
        response.CreatePlayerObject = true;
    }

    private void HandleConnectionApproval(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
    {
        response.CreatePlayerObject = true;

        // При StartHost Netcode прогоняет через подтверждение и самого хоста
        // (ClientNetworkId == ServerClientId). Спрашивать у человека разрешения
        // впустить самого себя незачем, поэтому одобряем молча.
        // Важно и технически: для хоста Netcode читает ответ сразу же и Pending
        // не поддерживает - отложенное решение здесь всё равно не сработало бы.
        if (request.ClientNetworkId == NetworkManager.ServerClientId)
        {
            response.Approved = true;
            return;
        }

        // Держим подключение открытым, пока хост не примет решение
        response.Pending = true;

        string displayName = "Игрок";

        if (SteamManager.TryReadSteamIdFromPayload(request.Payload, out CSteamID steamId) && SteamManager.Instance != null)
        {
            string personaName = SteamManager.Instance.GetPersonaName(steamId);
            if (!string.IsNullOrEmpty(personaName)) displayName = personaName;
        }

        _pendingRequests.Enqueue(new PendingRequest { Response = response, DisplayName = displayName });
    }

    private void Update()
    {
        // NetworkManager мог появиться позже нас - продолжаем попытки, пока не встанем
        if (!_callbackRegistered) TryRegisterCallback();

        if (_currentRequest == null)
        {
            ShowNextRequest();
            return;
        }

        if (autoDeclineSeconds <= 0f) return;

        _timeLeft -= Time.deltaTime;

        if (_timeLeft <= 0f)
        {
            Decide(false);
            return;
        }

        if (_dialogText != null)
        {
            _dialogText.SetText($"{_currentRequest.DisplayName} хочет присоединиться.\n\nАвтоматический отказ через {Mathf.CeilToInt(_timeLeft)} с");
        }
    }

    private void ShowNextRequest()
    {
        if (_pendingRequests.Count == 0) return;

        _currentRequest = _pendingRequests.Dequeue();
        _timeLeft = autoDeclineSeconds;

        EnsureDialogBuilt();

        if (_dialogRoot == null) return;

        _dialogRoot.SetActive(true);
        _dialogText.SetText($"{_currentRequest.DisplayName} хочет присоединиться.");
    }

    private void Decide(bool approved)
    {
        if (_currentRequest == null) return;

        _currentRequest.Response.Approved = approved;
        _currentRequest.Response.CreatePlayerObject = true;
        if (!approved) _currentRequest.Response.Reason = "Хост отклонил запрос на подключение.";

        // Снимаем Pending последним: именно по нему Netcode понимает, что решение принято
        _currentRequest.Response.Pending = false;
        _currentRequest = null;

        if (_dialogRoot != null) _dialogRoot.SetActive(false);
    }

    private void EnsureDialogBuilt()
    {
        if (_dialogRoot != null) return;

        Canvas canvas = uiCanvas != null ? uiCanvas : FindFirstObjectByType<Canvas>();

        if (canvas == null)
        {
            // Без Canvas окно не показать - принимаем, чтобы никто не завис навсегда
            Debug.LogError($"{nameof(JoinRequestApprovalUI)}: Canvas не найден, запросы будут приниматься автоматически.");
            Decide(true);
            return;
        }

        _dialogRoot = new GameObject("JoinRequestDialog", typeof(RectTransform), typeof(Image));
        var rootRect = (RectTransform)_dialogRoot.transform;
        rootRect.SetParent(canvas.transform, false);
        rootRect.anchorMin = new Vector2(0.5f, 0.5f);
        rootRect.anchorMax = new Vector2(0.5f, 0.5f);
        rootRect.pivot = new Vector2(0.5f, 0.5f);
        rootRect.anchoredPosition = Vector2.zero;
        rootRect.sizeDelta = new Vector2(420f, 190f);
        _dialogRoot.GetComponent<Image>().color = new Color(0.11f, 0.12f, 0.14f, 0.97f);

        var textObject = new GameObject("Text", typeof(RectTransform));
        var textRect = (RectTransform)textObject.transform;
        textRect.SetParent(rootRect, false);
        textRect.anchorMin = new Vector2(0f, 0.35f);
        textRect.anchorMax = new Vector2(1f, 1f);
        textRect.offsetMin = new Vector2(20f, 0f);
        textRect.offsetMax = new Vector2(-20f, -16f);

        _dialogText = textObject.AddComponent<TextMeshProUGUI>();
        _dialogText.fontSize = 19f;
        _dialogText.alignment = TextAlignmentOptions.Center;

        CreateDialogButton(rootRect, "Принять", new Vector2(0.06f, 0.08f), new Vector2(0.48f, 0.3f),
            new Color(0.22f, 0.5f, 0.28f, 1f), () => Decide(true));
        CreateDialogButton(rootRect, "Отклонить", new Vector2(0.52f, 0.08f), new Vector2(0.94f, 0.3f),
            new Color(0.5f, 0.24f, 0.24f, 1f), () => Decide(false));

        _dialogRoot.SetActive(false);
    }

    private static void CreateDialogButton(RectTransform parent, string label, Vector2 anchorMin, Vector2 anchorMax, Color color, UnityEngine.Events.UnityAction onClick)
    {
        var buttonObject = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button));
        var buttonRect = (RectTransform)buttonObject.transform;
        buttonRect.SetParent(parent, false);
        buttonRect.anchorMin = anchorMin;
        buttonRect.anchorMax = anchorMax;
        buttonRect.offsetMin = Vector2.zero;
        buttonRect.offsetMax = Vector2.zero;

        var image = buttonObject.GetComponent<Image>();
        image.color = Color.white;

        var button = buttonObject.GetComponent<Button>();
        var colors = button.colors;
        colors.normalColor = color;
        colors.highlightedColor = color * 1.25f;
        colors.pressedColor = color * 0.85f;
        colors.selectedColor = color;
        button.colors = colors;
        button.targetGraphic = image;
        button.onClick.AddListener(onClick);

        var labelObject = new GameObject("Label", typeof(RectTransform));
        var labelRect = (RectTransform)labelObject.transform;
        labelRect.SetParent(buttonRect, false);
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;

        var text = labelObject.AddComponent<TextMeshProUGUI>();
        text.SetText(label);
        text.fontSize = 17f;
        text.alignment = TextAlignmentOptions.Center;
    }
}
