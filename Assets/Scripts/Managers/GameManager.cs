using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Serialization;
using Unity.Netcode;
using TMPro;

// Режиссёр раунда. Живёт только в игровой сцене и работает ТОЛЬКО на сервере:
// клиенты получают готовые решения через сетевые переменные и RPC.
//
// Порядок фаз:
//   WaitingForPlayers -> Countdown -> Preparation -> RoleReveal
//                                                        |
//                                          HunterLocked -> Hunt -> RoundOver -> Lobby
//
// Победа охотника - подстрелил всех. Победа прячущихся - вышло время охоты.
public class GameManager : NetworkBehaviour
{
    [Header("Длительности фаз, сек")]
    [FormerlySerializedAs("gameStartCountdownDuration")]
    [SerializeField] private float countdownDuration = 5f;
    [FormerlySerializedAs("mapPreparationDuration")]
    [SerializeField] private float preparationDuration = 20f;
    [FormerlySerializedAs("hunterRevealCountdownDuration")]
    [SerializeField] private float roleRevealDuration = 5f;
    [Tooltip("Сколько охотник стоит запертым, пока остальные прячутся")]
    [FormerlySerializedAs("hunterReleaseDelay")]
    [SerializeField] private float hunterLockDuration = 10f;
    [Tooltip("Сколько у охотника есть на то, чтобы найти всех")]
    [SerializeField] private float huntDuration = 180f;
    [Tooltip("Сколько висит экран победы/поражения перед возвратом в лобби")]
    [SerializeField] private float resultScreenDuration = 6f;

    [Header("Правила")]
    [Tooltip("Сколько патронов выдаётся охотнику на каждого прячущегося")]
    [SerializeField] private int bulletsPerHider = 3;
    [SerializeField] private string lobbySceneName = "Lobby";

    [Header("Точки на карте")]
    [FormerlySerializedAs("hidersTeleportPoint")]
    [SerializeField] private Transform hidersSpawnPoint;
    [FormerlySerializedAs("hunterTeleportPoint")]
    [SerializeField] private Transform hunterSpawnPoint;
    [FormerlySerializedAs("startTeleportPoint")]
    [SerializeField] private Transform startSpawnPoint;
    [SerializeField] private Transform waitingForPlayersPoint;

    [Header("Интерфейс")]
    [SerializeField] private TextMeshProUGUI timerText;
    [SerializeField] private RoundResultUI resultUI;

    public NetworkVariable<GamePhase> Phase =
        new NetworkVariable<GamePhase>(GamePhase.WaitingForPlayers);

    private readonly List<PlayerState> _hiders = new List<PlayerState>();
    private HunterWeapon _hunterWeapon;
    private bool _roundStarted;
    private bool _roundFinished;

    private void Awake()
    {
        if (timerText != null) timerText.SetText("");
    }

    public override void OnNetworkSpawn()
    {
        // Появление GameManager означает "мы в игре": прячем курсор.
        // В лобби он нужен живым для кнопок, поэтому там этого кода нет.
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        if (resultUI != null) resultUI.Hide();

        if (!IsServer) return;

        NetworkManager.Singleton.SceneManager.OnLoadComplete += HandlePlayerFinishedLoadingScene;
        NetworkManager.Singleton.SceneManager.OnLoadEventCompleted += HandleSceneLoadedForAllPlayers;
    }

    public override void OnNetworkDespawn()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        if (!IsServer) return;

        NetworkManager.Singleton.SceneManager.OnLoadComplete -= HandlePlayerFinishedLoadingScene;
        NetworkManager.Singleton.SceneManager.OnLoadEventCompleted -= HandleSceneLoadedForAllPlayers;

        UnsubscribeFromRoundEvents();
    }

    // ---------- Ожидание игроков ----------

    // Срабатывает индивидуально: игрок догрузил сцену раньше остальных
    private void HandlePlayerFinishedLoadingScene(ulong clientId, string sceneName, LoadSceneMode loadSceneMode)
    {
        if (!TryGetPlayerController(clientId, out PlayerControllerNetCode player)) return;

        player.TeleportClientRpc(waitingForPlayersPoint.position);

        // Сообщение только ему: остальные его уже видели
        var targetOnly = new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
        };
        UpdateTimerTextClientRpc("Ожидание игроков...", targetOnly);
    }

    private void HandleSceneLoadedForAllPlayers(string sceneName, LoadSceneMode loadSceneMode,
        List<ulong> clientsCompleted, List<ulong> clientsTimedOut)
    {
        NetworkManager.Singleton.SceneManager.OnLoadComplete -= HandlePlayerFinishedLoadingScene;
        NetworkManager.Singleton.SceneManager.OnLoadEventCompleted -= HandleSceneLoadedForAllPlayers;

        if (_roundStarted) return;

        List<PlayerState> players = GetConnectedPlayerStates();

        if (players.Count < 2)
        {
            Debug.LogWarning("Нужно минимум 2 игрока для старта!");
            return;
        }

        _roundStarted = true;
        StartCoroutine(RunRound(players));
    }

    // ---------- Основной сценарий раунда ----------

    private IEnumerator RunRound(List<PlayerState> players)
    {
        // Игроки переживают смену сцены, поэтому состояние сбрасываем явно
        foreach (PlayerState player in players) player.ServerResetForNewRound();

        Phase.Value = GamePhase.Countdown;
        TeleportAll(players, startSpawnPoint.position);
        yield return RunCountdown(countdownDuration, "Игра начнётся через: ");

        Phase.Value = GamePhase.Preparation;
        TeleportAll(players, hidersSpawnPoint.position);
        yield return RunCountdown(preparationDuration, "Изучите карту: ");

        Phase.Value = GamePhase.RoleReveal;
        TeleportAll(players, startSpawnPoint.position);
        yield return new WaitForSeconds(0.1f);
        yield return RunCountdown(roleRevealDuration, "Выбор охотника через: ");

        PlayerState hunter = AssignRoles(players);

        Phase.Value = GamePhase.HunterLocked;
        yield return RunCountdown(hunterLockDuration, "Прячьтесь! Охотник выйдет через: ");

        Phase.Value = GamePhase.Hunt;

        if (hunter != null && hunter.TryGetComponent(out PlayerControllerNetCode hunterController))
        {
            hunterController.TeleportClientRpc(hunterSpawnPoint.position);
        }

        // Вырожденный случай: bulletsPerHider = 0, боезапас так и остался нулевым.
        // Событие изменения не сработает, потому что значение не менялось.
        if (_hunterWeapon != null && _hunterWeapon.Ammo.Value <= 0)
        {
            FinishRound(RoundOutcome.HidersWon);
            yield break;
        }

        yield return RunHuntPhase();
    }

    // Раздача ролей: один охотник, остальные прячущиеся
    private PlayerState AssignRoles(List<PlayerState> players)
    {
        int hunterIndex = Random.Range(0, players.Count);
        PlayerState hunter = null;

        _hiders.Clear();

        for (int i = 0; i < players.Count; i++)
        {
            PlayerState player = players[i];

            if (i == hunterIndex)
            {
                player.ServerSetRole(GameRole.Hunter);
                hunter = player;
            }
            else
            {
                player.ServerSetRole(GameRole.Hider);
                _hiders.Add(player);

                if (player.TryGetComponent(out PlayerControllerNetCode hiderController))
                {
                    hiderController.TeleportClientRpc(hidersSpawnPoint.position);
                }
            }
        }

        // Боезапас зависит от числа прячущихся: n патронов на каждого
        if (hunter != null && hunter.TryGetComponent(out HunterWeapon weapon))
        {
            weapon.ServerGrantAmmo(bulletsPerHider * _hiders.Count);

            // Кончились патроны - охотник больше никого не найдёт, раунд закрываем сразу
            weapon.Ammo.OnValueChanged += HandleHunterAmmoChanged;
            _hunterWeapon = weapon;
        }

        // Ловим момент, когда прячущегося подстрелили
        foreach (PlayerState hider in _hiders)
        {
            hider.IsAlive.OnValueChanged += HandleHiderAliveChanged;
        }

        return hunter;
    }

    private IEnumerator RunHuntPhase()
    {
        for (int secondsLeft = Mathf.CeilToInt(huntDuration); secondsLeft > 0; secondsLeft--)
        {
            // Охотник мог успеть раньше - тогда раунд закроет HandleHiderAliveChanged
            if (_roundFinished) yield break;

            UpdateTimerTextClientRpc($"Осталось: {secondsLeft}", default);
            yield return new WaitForSeconds(1f);
        }

        if (!_roundFinished) FinishRound(RoundOutcome.HidersWon);
    }

    private void HandleHiderAliveChanged(bool previous, bool current)
    {
        if (_roundFinished || current) return;

        foreach (PlayerState hider in _hiders)
        {
            if (hider != null && hider.IsAlive.Value) return;
        }

        FinishRound(RoundOutcome.HunterWon);
    }

    // Патроны кончились, а кто-то ещё прячется - искать больше нечем
    private void HandleHunterAmmoChanged(int previous, int current)
    {
        if (_roundFinished || current > 0) return;

        FinishRound(RoundOutcome.HidersWon);
    }

    // ---------- Завершение ----------

    private void FinishRound(RoundOutcome outcome)
    {
        if (_roundFinished) return;

        _roundFinished = true;
        Phase.Value = GamePhase.RoundOver;

        UnsubscribeFromRoundEvents();
        StopAllCoroutines();

        UpdateTimerTextClientRpc("", default);
        ShowResultClientRpc(outcome == RoundOutcome.HunterWon ? GameRole.Hunter : GameRole.Hider);

        StartCoroutine(ReturnToLobbyAfterDelay());
    }

    private IEnumerator ReturnToLobbyAfterDelay()
    {
        yield return new WaitForSeconds(resultScreenDuration);

        // Роли сбрасываем до смены сцены: объекты игроков переживают загрузку,
        // и без сброса роли утекли бы в следующий раунд
        foreach (PlayerState player in GetConnectedPlayerStates())
        {
            player.ServerResetForNewRound();
        }

        // Сетевая загрузка: соединение сохраняется, в лобби едут все разом
        NetworkManager.Singleton.SceneManager.LoadScene(lobbySceneName, LoadSceneMode.Single);
    }

    [ClientRpc]
    private void ShowResultClientRpc(GameRole winningRole)
    {
        if (resultUI != null)
        {
            // Каждый клиент сам сравнивает победившую роль со своей
            resultUI.Show(GetLocalPlayerRole() == winningRole);
        }

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    private GameRole GetLocalPlayerRole()
    {
        NetworkObject localPlayer = NetworkManager.Singleton.LocalClient.PlayerObject;

        if (localPlayer != null && localPlayer.TryGetComponent(out PlayerState state))
        {
            return state.Role.Value;
        }

        return GameRole.None;
    }

    private void UnsubscribeFromRoundEvents()
    {
        foreach (PlayerState hider in _hiders)
        {
            if (hider != null) hider.IsAlive.OnValueChanged -= HandleHiderAliveChanged;
        }

        _hiders.Clear();

        if (_hunterWeapon != null)
        {
            _hunterWeapon.Ammo.OnValueChanged -= HandleHunterAmmoChanged;
            _hunterWeapon = null;
        }
    }

    // ---------- Вспомогательное ----------

    private List<PlayerState> GetConnectedPlayerStates()
    {
        var players = new List<PlayerState>();

        foreach (var client in NetworkManager.Singleton.ConnectedClients)
        {
            if (client.Value.PlayerObject == null) continue;
            if (client.Value.PlayerObject.TryGetComponent(out PlayerState state)) players.Add(state);
        }

        return players;
    }

    private bool TryGetPlayerController(ulong clientId, out PlayerControllerNetCode player)
    {
        player = null;

        if (!NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out var client)) return false;
        if (client.PlayerObject == null) return false;

        return client.PlayerObject.TryGetComponent(out player);
    }

    private void TeleportAll(List<PlayerState> players, Vector3 position)
    {
        foreach (PlayerState player in players)
        {
            if (player.TryGetComponent(out PlayerControllerNetCode controller))
            {
                controller.TeleportClientRpc(position);
            }
        }
    }

    private IEnumerator RunCountdown(float durationSeconds, string messagePrefix)
    {
        for (int secondsLeft = Mathf.CeilToInt(durationSeconds); secondsLeft > 0; secondsLeft--)
        {
            UpdateTimerTextClientRpc(messagePrefix + secondsLeft, default);
            yield return new WaitForSeconds(1f);
        }

        UpdateTimerTextClientRpc("", default);
    }

    [ClientRpc]
    private void UpdateTimerTextClientRpc(string text, ClientRpcParams rpcParams = default)
    {
        if (timerText != null) timerText.SetText(text);
    }
}
