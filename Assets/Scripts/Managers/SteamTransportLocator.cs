using Netcode.Transports;
using Unity.Netcode;
using UnityEngine;

// Находит транспорт Steam у живого NetworkManager.
//
// Раньше транспорт таскали ссылкой в инспекторе, но после переезда NetworkManager
// в сцену Bootstrap это перестало работать: Unity НЕ УМЕЕТ хранить ссылки между
// сценами, и поля молча обнулялись. Поэтому берём транспорт в рантайме у самого
// NetworkManager - он всегда знает, каким транспортом пользуется.
public static class SteamTransportLocator
{
    public static SteamNetworkingSocketsTransport Get()
    {
        if (NetworkManager.Singleton == null)
        {
            Debug.LogError("NetworkManager не найден. Убедитесь, что сцена Bootstrap загружается первой.");
            return null;
        }

        var transport = NetworkManager.Singleton.NetworkConfig.NetworkTransport as SteamNetworkingSocketsTransport;

        if (transport == null)
        {
            Debug.LogError("В NetworkManager не назначен SteamNetworkingSocketsTransport - подключение по Steam невозможно.");
        }

        return transport;
    }

    // Подставляет SteamID хоста и подключается. Общий путь и для оверлея, и для списка друзей.
    public static bool TryConnectTo(ulong hostSteamId)
    {
        SteamNetworkingSocketsTransport transport = Get();
        if (transport == null) return false;

        // Транспорт адресуется по CSteamID, а не по IP
        transport.ConnectToSteamID = hostSteamId;
        NetworkManager.Singleton.StartClient();
        return true;
    }
}
