using System;
using Unity.Netcode;
using UnityEngine;

// Сетевое состояние одного игрока: роль, жив ли, какой скин.
//
// Это единственный источник правды. Менять значения имеет право ТОЛЬКО сервер,
// остальные компоненты (управление, оружие, наблюдение) читают их и подписываются
// на события. Так убирается главная проблема старого кода, где роль, камера и
// внешний вид были перемешаны в одном классе.
//
// Полей в инспекторе нет специально: компонент добавляется на префаб игрока
// одним нажатием Add Component, ничего перетаскивать не нужно.
[RequireComponent(typeof(NetworkObject))]
public class PlayerState : NetworkBehaviour
{
    public NetworkVariable<GameRole> Role = new NetworkVariable<GameRole>(GameRole.None);
    public NetworkVariable<bool> IsAlive = new NetworkVariable<bool>(true);
    public NetworkVariable<int> ModelIndex = new NetworkVariable<int>(0);

    // На эти события подписываются "виды": управление, внешность, оружие
    public event Action<GameRole> RoleChanged;
    public event Action<bool> AliveChanged;
    public event Action<int> ModelChanged;

    public bool IsHunter => Role.Value == GameRole.Hunter;
    public bool IsHider => Role.Value == GameRole.Hider;
    // Прячущийся, которого ещё не подстрелили
    public bool IsPlayableHider => IsHider && IsAlive.Value;

    public override void OnNetworkSpawn()
    {
        Role.OnValueChanged += HandleRoleChanged;
        IsAlive.OnValueChanged += HandleAliveChanged;
        ModelIndex.OnValueChanged += HandleModelChanged;

        // Тем, кто подключился позже, нужно сразу применить текущее состояние
        RoleChanged?.Invoke(Role.Value);
        AliveChanged?.Invoke(IsAlive.Value);
        ModelChanged?.Invoke(ModelIndex.Value);
    }

    public override void OnNetworkDespawn()
    {
        Role.OnValueChanged -= HandleRoleChanged;
        IsAlive.OnValueChanged -= HandleAliveChanged;
        ModelIndex.OnValueChanged -= HandleModelChanged;
    }

    private void HandleRoleChanged(GameRole previous, GameRole current) => RoleChanged?.Invoke(current);
    private void HandleAliveChanged(bool previous, bool current) => AliveChanged?.Invoke(current);
    private void HandleModelChanged(int previous, int current) => ModelChanged?.Invoke(current);

    // ---------- Серверное API ----------

    public void ServerSetRole(GameRole role)
    {
        if (!IsServer) return;
        Role.Value = role;
    }

    public void ServerSetAlive(bool alive)
    {
        if (!IsServer) return;
        IsAlive.Value = alive;
    }

    // Вызывается перед возвратом в лобби: игрок переживает смену сцены,
    // поэтому состояние нужно сбросить руками, иначе роли утекут в новый раунд.
    public void ServerResetForNewRound()
    {
        if (!IsServer) return;

        Role.Value = GameRole.None;
        IsAlive.Value = true;
        ModelIndex.Value = 0;
    }

    // ---------- Запрос от клиента ----------

    [ServerRpc]
    public void RequestModelServerRpc(int index)
    {
        // Сервер проверяет право на смену скина сам - клиенту доверять нельзя
        if (!IsPlayableHider) return;

        ModelIndex.Value = index;
    }
}
