using Steamworks;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class LobbyUIController : MonoBehaviour
{
    [Header("Connection Buttons")]
    [SerializeField] private Button buttonHost;
    [SerializeField] private Button buttonDisconnect;

    [Header("Steam Overlay Invite")]
    [Tooltip("Оверлей доступен, только когда игра запущена через Steam. На macOS он может не отрисоваться - там используйте список друзей.")]
    [SerializeField] private Button buttonInvite;

    [Header("Start Game")]
    [SerializeField] private Button buttonStartGame;
    [SerializeField] private string gameplaySceneName = "Game";

    private void Awake()
    {
        // Понятное сообщение вместо NullReferenceException, если поле забыли назначить
        if (!IsAssigned(buttonHost, nameof(buttonHost))) return;
        if (!IsAssigned(buttonDisconnect, nameof(buttonDisconnect))) return;
        if (!IsAssigned(buttonInvite, nameof(buttonInvite))) return;
        if (!IsAssigned(buttonStartGame, nameof(buttonStartGame))) return;

        buttonStartGame.gameObject.SetActive(false);
        buttonInvite.gameObject.SetActive(false);

        SteamManager.OnLobbyEntered += HandleSteamLobbyEntered;

        buttonHost.onClick.AddListener(() =>
        {
            if (!SteamManager.IsSteamInitialized)
            {
                Debug.LogWarning("Steam не инициализирован - хост не поднять. Смотрите причину выше в логе.");
                return;
            }

            // Хост поднимаем сразу, не дожидаясь Steam-лобби: если создание лобби
            // сорвётся, игра всё равно работает, просто без приглашений через оверлей.
            NetworkManager.Singleton.StartHost();

            // Способ 1: лобби нужно для приглашений через оверлей Steam
            SteamManager.Instance.CreateLobby();

            // Способ 2: объявляем себя друзьям для внутриигрового списка
            SteamManager.Instance.SetHostingPresence();

            buttonStartGame.gameObject.SetActive(true);
            buttonInvite.gameObject.SetActive(true);
        });
        buttonDisconnect.onClick.AddListener(() =>
        {
            NetworkManager.Singleton.Shutdown();

            if (SteamManager.IsSteamInitialized)
            {
                SteamManager.Instance.ClearHostingPresence();
            }

            buttonStartGame.gameObject.SetActive(false);
            buttonInvite.gameObject.SetActive(false);
        });
        buttonInvite.onClick.AddListener(() =>
        {
            SteamManager.Instance.OpenInviteOverlay();
        });
        buttonStartGame.onClick.AddListener(() =>
        {
            // Загружает игровую сцену у всех подключённых клиентов.
            // Игроки переместятся на неё автоматически, когда сцена будет готова у всех (см. GameManager.OnLoadEventCompleted).
            if (!NetworkManager.Singleton.IsServer) return;

            NetworkManager.Singleton.SceneManager.LoadScene(gameplaySceneName, LoadSceneMode.Single);
        });
    }

    private void OnDestroy()
    {
        SteamManager.OnLobbyEntered -= HandleSteamLobbyEntered;
    }

    // Срабатывает и когда мы создали своё лобби (хост), и когда зашли в чужое по приглашению
    private void HandleSteamLobbyEntered(CSteamID lobbyId)
    {
        if (NetworkManager.Singleton == null || SteamManager.Instance == null) return;

        // Уже в сети - значит это наше собственное лобби (мы хост) либо мы успели
        // подключиться через список друзей. Второй раз подключаться не нужно.
        if (NetworkManager.Singleton.IsClient || NetworkManager.Singleton.IsServer) return;

        CSteamID lobbyOwner = SteamManager.Instance.GetLobbyOwner(lobbyId);

        if (lobbyOwner == SteamUser.GetSteamID()) return;

        // Хост покажет наше имя в окне подтверждения
        NetworkManager.Singleton.NetworkConfig.ConnectionData = SteamManager.Instance.BuildConnectionPayload();

        // Приняли приглашение через оверлей - подключаемся к владельцу лобби
        SteamTransportLocator.TryConnectTo(lobbyOwner.m_SteamID);
    }

    private bool IsAssigned(UnityEngine.Object field, string fieldName)
    {
        if (field != null) return true;

        Debug.LogError($"В {nameof(LobbyUIController)} не назначено поле {fieldName} - интерфейс лобби работать не будет.");
        return false;
    }
}
