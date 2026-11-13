using Unity.Netcode;
using UnityEngine;

// Живёт в сцене лобби. Как только игрок впервые подключился к серверу (и у него
// заспавнен PlayerObject), ставит его в точку ожидания остальных игроков.
[RequireComponent(typeof(NetworkObject))]
public class LobbyManager : NetworkBehaviour
{
    [SerializeField] private Transform waitingForPlayersPoint;

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;

        NetworkManager.Singleton.OnClientConnectedCallback += HandleClientConnected;
    }

    public override void OnNetworkDespawn()
    {
        if (!IsServer) return;

        NetworkManager.Singleton.OnClientConnectedCallback -= HandleClientConnected;
    }

    private void HandleClientConnected(ulong clientId)
    {
        if (!NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out var client)) return;
        if (client.PlayerObject == null) return;
        if (!client.PlayerObject.TryGetComponent(out PlayerControllerNetCode player)) return;

        player.TeleportClientRpc(waitingForPlayersPoint.position);
    }
}
