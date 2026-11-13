using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

// Вешается на объект в сцене Bootstrap. Сама Bootstrap ничего не показывает -
// она существует ради NetworkManager и SteamManager, которые помечены
// DontDestroyOnLoad и потому переживают любые последующие загрузки сцен.
//
// Зачем это нужно: NetworkManager нельзя держать в сцене Lobby. При возврате
// из игры Netcode загружает лобби заново, и в сцене появился бы ВТОРОЙ
// NetworkManager, а Netcode дубликаты не уничтожает - сеть бы сломалась.
public class BootstrapSceneLoader : MonoBehaviour
{
    [Tooltip("Сцена, которая загрузится сразу после инициализации")]
    [SerializeField] private string firstSceneName = SessionBootstrap.LobbySceneName;

    [Tooltip("Небольшая задержка, чтобы Steam успел проинициализироваться")]
    [SerializeField] private float delaySeconds = 0.1f;

    private IEnumerator Start()
    {
        if (delaySeconds > 0f) yield return new WaitForSeconds(delaySeconds);

        // Если в сцене есть заставка студии - сначала отыгрываем её.
        // Ссылку не тянем через инспектор: находим сами, чтобы нечему было слетать.
        var splash = FindFirstObjectByType<StudioSplash>();
        if (splash != null) yield return splash.Play();

        // Обычная загрузка Unity, а не через NetworkSceneManager: сети ещё нет,
        // мы даже не подключены. Bootstrap выгрузится, менеджеры переживут.
        SceneManager.LoadScene(firstSceneName, LoadSceneMode.Single);
    }
}
