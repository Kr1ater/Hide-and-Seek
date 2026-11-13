using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

// Создаётся автоматически при запуске игры, живёт между сценами.
// Решает две задачи, которые иначе ломают выход из сессии:
//
// 1. Запасная камера. Единственная камера в проекте лежит внутри Player.prefab,
//    поэтому при дисконнекте объект игрока уничтожается вместе с камерой и в сцене
//    не остаётся ни одной. Unity перестаёт очищать экран, и поверх застывшего кадра
//    мерцает UI. Запасная камера включается ровно тогда, когда других не осталось.
//
// 2. Возврат в лобби. NetworkManager.Shutdown() не выгружает игровую сцену сам,
//    поэтому после отключения игрок физически оставался в игровой сцене.
public class SessionBootstrap : MonoBehaviour
{
    // Сцена лобби. Объект создаётся кодом и полей в инспекторе не имеет,
    // поэтому имя задаётся здесь. Меняется в одном месте на весь проект.
    public const string LobbySceneName = "Lobby";

    private static SessionBootstrap _instance;

    private Camera _fallbackCamera;
    private AudioListener _fallbackAudioListener;
    private string _lobbySceneName;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (_instance != null) return;

        var host = new GameObject("SessionBootstrap");
        _instance = host.AddComponent<SessionBootstrap>();
        DontDestroyOnLoad(host);
    }

    private void Awake()
    {
        // Раньше за лобби принималась стартовая сцена. С появлением отдельной
        // сцены Bootstrap это перестало работать: стартовой стала именно она,
        // и после разрыва связи игрока выкидывало в пустой Bootstrap.
        _lobbySceneName = LobbySceneName;

        CreateFallbackCamera();
    }

    private void CreateFallbackCamera()
    {
        var cameraObject = new GameObject("FallbackCamera");
        cameraObject.transform.SetParent(transform, false);

        _fallbackCamera = cameraObject.AddComponent<Camera>();
        _fallbackCamera.clearFlags = CameraClearFlags.SolidColor;
        _fallbackCamera.backgroundColor = new Color(0.06f, 0.07f, 0.09f, 1f);
        // Ниже камер игрока, чтобы никогда не перекрывать игровую картинку
        _fallbackCamera.depth = -100f;

        _fallbackAudioListener = cameraObject.AddComponent<AudioListener>();
    }

    private void LateUpdate()
    {
        if (_fallbackCamera == null) return;

        // Camera.allCamerasCount считает только включённые камеры, включая нашу.
        // Так обходимся без аллокаций, которые даёт Camera.allCameras.
        bool shouldBeActive = _fallbackCamera.enabled
            ? Camera.allCamerasCount <= 1
            : Camera.allCamerasCount == 0;

        if (_fallbackCamera.enabled == shouldBeActive) return;

        _fallbackCamera.enabled = shouldBeActive;
        // Второй AudioListener в сцене вызывает предупреждение Unity, поэтому
        // держим его строго в паре с камерой
        _fallbackAudioListener.enabled = shouldBeActive;
    }

    private void OnEnable()
    {
        Subscribe();
    }

    private void Update()
    {
        // NetworkManager может появиться позже нас (или после перезагрузки сцены)
        Subscribe();
    }

    // Храним не флаг, а сам NetworkManager, на который подписаны. После возврата
    // в лобби менеджер уже ДРУГОЙ объект, и по простому флагу мы бы решили, что
    // подписка есть, и молча перестали реагировать на второй выход из игры.
    private NetworkManager _subscribedManager;

    private void Subscribe()
    {
        NetworkManager current = NetworkManager.Singleton;

        if (current == null || _subscribedManager == current) return;

        current.OnServerStopped += HandleSessionStopped;
        current.OnClientStopped += HandleSessionStopped;
        _subscribedManager = current;
    }

    private void OnDisable()
    {
        if (_subscribedManager == null) return;

        _subscribedManager.OnServerStopped -= HandleSessionStopped;
        _subscribedManager.OnClientStopped -= HandleSessionStopped;
        _subscribedManager = null;
    }

    private bool _returningToLobby;

    // Сессия закончилась: сам отключился, хост закрыл игру или отклонил запрос
    private void HandleSessionStopped(bool wasHost)
    {
        // У хоста Shutdown() поднимает СРАЗУ ДВА события - OnServerStopped и
        // OnClientStopped. Проверять здесь активную сцену бесполезно: LoadScene
        // отложенный, и на момент второго вызова сцена ещё прежняя. Без этого
        // флага лобби грузилось бы дважды за кадр.
        if (_returningToLobby) return;

        if (SceneManager.GetActiveScene().name == _lobbySceneName) return;

        _returningToLobby = true;
        StartCoroutine(ReturnToLobbyRoutine());
    }

    private IEnumerator ReturnToLobbyRoutine()
    {
        // NetworkManager помечает себя DontDestroyOnLoad и НЕ уничтожает дубликаты:
        // при перезагрузке сцены лобби появился бы второй, потом третий и так далее.
        // Проверено по исходникам Netcode - в его OnEnable есть только
        // "if (Singleton == null) SetSingleton()", самоуничтожения нет.
        //
        // Поэтому старый NetworkManager сносим сами: сессия уже закрыта, а свежая
        // сцена лобби создаст новый, на который корректно указывают все ссылки
        // в инспекторе (транспорт, кнопки и прочее).
        if (NetworkManager.Singleton != null)
        {
            Destroy(NetworkManager.Singleton.gameObject);
        }

        // Ждём кадр, чтобы объект действительно уничтожился до загрузки сцены
        yield return null;

        SceneManager.LoadScene(_lobbySceneName);

        _returningToLobby = false;
    }
}
