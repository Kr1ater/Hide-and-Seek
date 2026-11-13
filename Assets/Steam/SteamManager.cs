using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Steamworks;
using UnityEngine;

// Инициализация Steam API + два независимых способа собрать друзей в игру:
//
// 1. Оверлей Steam (ActivateGameOverlayInviteDialog) - отлично работает на Windows,
//    но требует запуска игры ЧЕРЕЗ Steam и сломан на Apple Silicon.
// 2. Внутриигровой список друзей через rich presence - работает везде, включая macOS,
//    и не зависит от оверлея вообще.
//
// Оба пути живут параллельно и не мешают друг другу: игрок пользуется тем,
// который доступен на его платформе.
public class SteamManager : MonoBehaviour
{
    [Header("Steam")]
    [Tooltip("480 = Spacewar, тестовое приложение Valve. Заменить на свой AppID перед релизом в Steam.")]
    [SerializeField] private string steamAppId = "480";

    public static SteamManager Instance { get; private set; }
    public static bool IsSteamInitialized { get; private set; }

    // Вызывается, когда локальный игрок оказался в Steam-лобби (создал своё или зашёл в чужое)
    public static event Action<CSteamID> OnLobbyEntered;

    private const int MaxLobbyMembers = 8;

    private CSteamID _currentLobbyId;

    private Callback<LobbyCreated_t> _lobbyCreatedCallback;
    private Callback<GameLobbyJoinRequested_t> _gameLobbyJoinRequestedCallback;
    private Callback<LobbyEnter_t> _lobbyEnteredCallback;
    private Callback<AvatarImageLoaded_t> _avatarImageLoadedCallback;

    // Аватарки тяжёлые, поэтому строим Texture2D один раз на друга
    private readonly Dictionary<ulong, Texture2D> _avatarCache = new Dictionary<ulong, Texture2D>();

    // Только тот экземпляр, который реально поднял Steam API, имеет право его выключать.
    // Без этого флага дубликат (например, при повторной загрузке сцены лобби) в своём
    // OnDestroy вызвал бы SteamAPI.Shutdown() и вырубил Steam у живого SteamManager.
    private bool _ownsSteamApi;

    private void Awake()
    {
        if (Instance != null)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

        IsSteamInitialized = InitializeSteam();
        _ownsSteamApi = IsSteamInitialized;

        if (!IsSteamInitialized) return;

        _lobbyCreatedCallback = Callback<LobbyCreated_t>.Create(HandleLobbyCreated);
        _gameLobbyJoinRequestedCallback = Callback<GameLobbyJoinRequested_t>.Create(HandleGameLobbyJoinRequested);
        _lobbyEnteredCallback = Callback<LobbyEnter_t>.Create(HandleLobbyEntered);
        _avatarImageLoadedCallback = Callback<AvatarImageLoaded_t>.Create(HandleAvatarImageLoaded);
    }

    // Нативные setenv: managed-обёртка Environment.SetEnvironmentVariable не на всех
    // рантаймах доходит до getenv(), который читает библиотека Steam, поэтому дублируем.
    [DllImport("libc", EntryPoint = "setenv")]
    private static extern int NativeSetEnv(string name, string value, int overwrite);

    [DllImport("msvcrt", EntryPoint = "_putenv_s")]
    private static extern int NativePutEnv(string name, string value);

    // Когда игра запущена ЧЕРЕЗ Steam как "не-Steam игра", Steam кладёт в SteamAppId
    // идентификатор своего ярлыка. Это не настоящее приложение, поэтому SteamAPI_Init
    // падает, а steam_appid.txt не помогает: переменная окружения важнее файла.
    // Перебиваем её на наш AppID - тогда работает и API, и оверлей (он внедряется
    // только в процессы, запущенные самим Steam).
    private void ForceSteamAppId()
    {
        string launchedWith = Environment.GetEnvironmentVariable("SteamAppId");

        Environment.SetEnvironmentVariable("SteamAppId", steamAppId);
        Environment.SetEnvironmentVariable("SteamGameId", steamAppId);

        try
        {
            bool isWindows = Application.platform == RuntimePlatform.WindowsPlayer
                             || Application.platform == RuntimePlatform.WindowsEditor;

            if (isWindows)
            {
                NativePutEnv("SteamAppId", steamAppId);
                NativePutEnv("SteamGameId", steamAppId);
            }
            else
            {
                NativeSetEnv("SteamAppId", steamAppId, 1);
                NativeSetEnv("SteamGameId", steamAppId, 1);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Не удалось выставить SteamAppId системным вызовом: {e.Message}");
        }

        if (!string.IsNullOrEmpty(launchedWith) && launchedWith != steamAppId)
        {
            Debug.Log($"Игра запущена через Steam с AppID {launchedWith} (ярлык не-Steam игры), подменяем на {steamAppId}.");
        }
    }

    private bool InitializeSteam()
    {
        ForceSteamAppId();

        // Steam ищет steam_appid.txt в ТЕКУЩЕМ РАБОЧЕМ КАТАЛОГЕ процесса.
        // На macOS при запуске .app (из Finder или из Steam) рабочий каталог = "/",
        // поэтому файл рядом с исполняемым файлом не находится и Init падает.
        // StreamingAssets подходит идеально: Unity копирует его содержимое в любую
        // сборку на любой платформе, и путь к нему известен в рантайме.
        string previousWorkingDirectory = Directory.GetCurrentDirectory();
        bool switchedDirectory = false;

        if (Directory.Exists(Application.streamingAssetsPath))
        {
            Directory.SetCurrentDirectory(Application.streamingAssetsPath);
            switchedDirectory = true;
        }

        try
        {
            // InitEx, а не Init: Init возвращает голый bool и выбрасывает текст ошибки,
            // из-за чего причину отказа приходится угадывать.
            ESteamAPIInitResult result = SteamAPI.InitEx(out string steamErrorMessage);

            if (result == ESteamAPIInitResult.k_ESteamAPIInitResult_OK) return true;

            string appIdFile = Path.Combine(Directory.GetCurrentDirectory(), "steam_appid.txt");

            Debug.LogError(
                $"Steam API не инициализировался.\n" +
                $"Причина ({result}): {steamErrorMessage}\n" +
                $"Рабочий каталог: {Directory.GetCurrentDirectory()}\n" +
                $"steam_appid.txt найден: {File.Exists(appIdFile)}" +
                (File.Exists(appIdFile) ? $" (AppID в файле: {File.ReadAllText(appIdFile).Trim()})" : "") + "\n" +
                $"Переменная окружения SteamAppId: {Environment.GetEnvironmentVariable("SteamAppId") ?? "не задана"}");

            return false;
        }
        finally
        {
            // Возвращаем каталог обратно, чтобы не влиять на остальной код игры
            if (switchedDirectory) Directory.SetCurrentDirectory(previousWorkingDirectory);
        }
    }

    private void Update()
    {
        if (!_ownsSteamApi) return;

        SteamAPI.RunCallbacks();
    }

    private void OnDestroy()
    {
        if (!_ownsSteamApi) return;

        _ownsSteamApi = false;
        IsSteamInitialized = false;
        Instance = null;

        SteamAPI.Shutdown();
    }

    // Ключ rich presence, по которому друзья понимают, что мы подняли сервер.
    // Оверлей Steam для этого не нужен - данные идут через клиент Steam.
    private const string HostingPresenceKey = "hosting";

    public void SetHostingPresence()
    {
        SteamFriends.SetRichPresence(HostingPresenceKey, "1");
        SteamFriends.SetRichPresence("status", "Хостит игру");
    }

    public void ClearHostingPresence()
    {
        SteamFriends.ClearRichPresence();
    }

    public string GetPersonaName(CSteamID steamId)
    {
        return SteamFriends.GetFriendPersonaName(steamId);
    }

    // Клиент прикладывает свой SteamID к запросу на подключение,
    // чтобы хост показал в окне подтверждения имя, а не голый номер.
    public byte[] BuildConnectionPayload()
    {
        return BitConverter.GetBytes(SteamUser.GetSteamID().m_SteamID);
    }

    public static bool TryReadSteamIdFromPayload(byte[] payload, out CSteamID steamId)
    {
        steamId = default;

        if (payload == null || payload.Length < sizeof(ulong)) return false;

        steamId = new CSteamID(BitConverter.ToUInt64(payload, 0));
        return true;
    }

    // Друзья, которые прямо сейчас запустили эту же игру.
    // isHosting = true, если друг поднял сервер и к нему можно подключиться.
    public List<SteamFriendInfo> GetFriendsInGame()
    {
        var result = new List<SteamFriendInfo>();

        if (!IsSteamInitialized) return result;

        uint ourAppId = SteamUtils.GetAppID().m_AppId;
        int friendCount = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);

        for (int i = 0; i < friendCount; i++)
        {
            CSteamID friendId = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);

            // Играет ли друг во что-то прямо сейчас и то ли это приложение
            if (!SteamFriends.GetFriendGamePlayed(friendId, out FriendGameInfo_t gameInfo)) continue;
            if (gameInfo.m_gameID.AppID().m_AppId != ourAppId) continue;

            result.Add(new SteamFriendInfo
            {
                SteamId = friendId,
                Name = SteamFriends.GetFriendPersonaName(friendId),
                IsHosting = SteamFriends.GetFriendRichPresence(friendId, HostingPresenceKey) == "1"
            });
        }

        return result;
    }

    // ---------- Способ 1: Steam-лобби и оверлей приглашений (Windows) ----------

    public void CreateLobby()
    {
        SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, MaxLobbyMembers);
    }

    public void OpenInviteOverlay()
    {
        if (!_currentLobbyId.IsValid())
        {
            Debug.LogWarning("Steam-лобби ещё не создано, приглашать некуда. Сначала нажмите Host.");
            return;
        }

        if (!SteamUtils.IsOverlayEnabled())
        {
            Debug.LogWarning(
                "Оверлей Steam недоступен: игра запущена не через Steam, либо оверлей выключен в настройках Steam. " +
                "Пригласить можно через внутриигровой список друзей - друг увидит вас там, как только запустит игру.");
            return;
        }

        SteamFriends.ActivateGameOverlayInviteDialog(_currentLobbyId);
    }

    private void HandleLobbyCreated(LobbyCreated_t callback)
    {
        if (callback.m_eResult != EResult.k_EResultOK)
        {
            // Не критично: игра уже хостится, просто приглашения через оверлей недоступны
            Debug.LogWarning($"Не удалось создать Steam-лобби ({callback.m_eResult}). Приглашения через оверлей будут недоступны, список друзей продолжит работать.");
            return;
        }

        _currentLobbyId = new CSteamID(callback.m_ulSteamIDLobby);
    }

    private void HandleGameLobbyJoinRequested(GameLobbyJoinRequested_t callback)
    {
        // Игрок принял приглашение через оверлей/список друзей Steam
        SteamMatchmaking.JoinLobby(callback.m_steamIDLobby);
    }

    private void HandleLobbyEntered(LobbyEnter_t callback)
    {
        _currentLobbyId = new CSteamID(callback.m_ulSteamIDLobby);
        OnLobbyEntered?.Invoke(_currentLobbyId);
    }

    public CSteamID GetLobbyOwner(CSteamID lobbyId)
    {
        return SteamMatchmaking.GetLobbyOwner(lobbyId);
    }

    // ---------- Аватарки ----------

    // Возвращает аватарку друга или null, если она ещё не подгрузилась.
    // Steam догружает её асинхронно и присылает AvatarImageLoaded_t - тогда кэш обновится.
    public Texture2D GetFriendAvatar(CSteamID friendId)
    {
        if (!IsSteamInitialized) return null;

        if (_avatarCache.TryGetValue(friendId.m_SteamID, out Texture2D cached) && cached != null)
        {
            return cached;
        }

        int avatarHandle = SteamFriends.GetMediumFriendAvatar(friendId);

        // 0 - у друга нет аватарки, -1 - Steam ещё качает её (придёт AvatarImageLoaded_t)
        if (avatarHandle <= 0) return null;

        Texture2D avatar = BuildTextureFromSteamImage(avatarHandle);
        if (avatar != null) _avatarCache[friendId.m_SteamID] = avatar;

        return avatar;
    }

    private void HandleAvatarImageLoaded(AvatarImageLoaded_t callback)
    {
        Texture2D avatar = BuildTextureFromSteamImage(callback.m_iImage);
        if (avatar != null) _avatarCache[callback.m_steamID.m_SteamID] = avatar;
    }

    private Texture2D BuildTextureFromSteamImage(int imageHandle)
    {
        if (!SteamUtils.GetImageSize(imageHandle, out uint width, out uint height)) return null;
        if (width == 0 || height == 0) return null;

        int byteCount = (int)(width * height * 4);
        var rgba = new byte[byteCount];

        if (!SteamUtils.GetImageRGBA(imageHandle, rgba, byteCount)) return null;

        // Steam отдаёт строки сверху вниз, а Unity ждёт снизу вверх - переворачиваем,
        // иначе аватарка будет вверх ногами.
        var flipped = new byte[byteCount];
        int rowBytes = (int)width * 4;

        for (int row = 0; row < height; row++)
        {
            Buffer.BlockCopy(rgba, row * rowBytes, flipped, (int)(height - 1 - row) * rowBytes, rowBytes);
        }

        var texture = new Texture2D((int)width, (int)height, TextureFormat.RGBA32, false);
        texture.LoadRawTextureData(flipped);
        texture.Apply();

        return texture;
    }
}

public struct SteamFriendInfo
{
    public CSteamID SteamId;
    public string Name;
    public bool IsHosting;
}