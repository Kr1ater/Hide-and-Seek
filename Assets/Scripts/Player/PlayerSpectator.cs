using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

// Режим наблюдателя для подстреленного прячущегося.
//
// Своё тело он больше не контролирует, зато его камера от третьего лица
// цепляется к живым игрокам. Переключение между ними - пробел или ЛКМ.
//
// Работает только у владельца: чужие камеры трогать не нужно.
[RequireComponent(typeof(PlayerState))]
[RequireComponent(typeof(PlayerControllerNetCode))]
public class PlayerSpectator : NetworkBehaviour
{
    [Header("Управление")]
    [SerializeField] private KeyCode nextTargetKey = KeyCode.Space;

    [Header("Подсказка на экране (необязательно)")]
    [SerializeField] private GameObject spectatorHintPanel;

    private PlayerState _state;
    private PlayerControllerNetCode _controller;

    private readonly List<Transform> _targets = new List<Transform>();
    private int _targetIndex;
    private bool _isActive;

    private void Awake()
    {
        _state = GetComponent<PlayerState>();
        _controller = GetComponent<PlayerControllerNetCode>();

        if (spectatorHintPanel != null) spectatorHintPanel.SetActive(false);
    }

    public override void OnNetworkSpawn()
    {
        if (!IsOwner) return;

        _state.AliveChanged += HandleAliveChanged;
        HandleAliveChanged(_state.IsAlive.Value);
    }

    public override void OnNetworkDespawn()
    {
        if (!IsOwner) return;

        _state.AliveChanged -= HandleAliveChanged;
    }

    private void HandleAliveChanged(bool alive)
    {
        if (alive) StopSpectating();
        else StartSpectating();
    }

    private void StartSpectating()
    {
        _isActive = true;
        _controller.IsSpectating = true;

        if (spectatorHintPanel != null) spectatorHintPanel.SetActive(true);

        RefreshTargets();
        FocusCurrentTarget();
    }

    private void StopSpectating()
    {
        if (!_isActive) return;

        _isActive = false;
        _controller.IsSpectating = false;

        if (spectatorHintPanel != null) spectatorHintPanel.SetActive(false);

        // Возвращаем камеру на собственное тело
        SetCameraTarget(transform);
    }

    private void Update()
    {
        if (!_isActive || !IsOwner) return;

        if (Input.GetKeyDown(nextTargetKey) || Input.GetMouseButtonDown(0))
        {
            RefreshTargets();
            _targetIndex++;
            FocusCurrentTarget();
        }
    }

    // Список живых игроков строим заново при каждом переключении:
    // за это время кого-то могли подстрелить
    private void RefreshTargets()
    {
        _targets.Clear();

        if (NetworkManager.Singleton == null) return;

        foreach (var client in NetworkManager.Singleton.ConnectedClientsList)
        {
            if (client.PlayerObject == null) continue;
            if (client.PlayerObject == NetworkObject) continue;

            if (!client.PlayerObject.TryGetComponent(out PlayerState other)) continue;
            if (!other.IsAlive.Value) continue;

            _targets.Add(other.transform);
        }
    }

    private void FocusCurrentTarget()
    {
        if (_targets.Count == 0)
        {
            SetCameraTarget(transform);
            return;
        }

        _targetIndex = (_targetIndex % _targets.Count + _targets.Count) % _targets.Count;
        SetCameraTarget(_targets[_targetIndex]);
    }

    private void SetCameraTarget(Transform target)
    {
        var camera = _controller.ThirdPersonCamera;
        if (camera == null || target == null) return;

        // Cinemachine сам доведёт камеру до новой цели
        camera.Follow = target;
        camera.LookAt = target;
        camera.Priority.Value = 1;
    }
}
