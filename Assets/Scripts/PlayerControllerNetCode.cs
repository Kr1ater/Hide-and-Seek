using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using Unity.Cinemachine;

// Управление игроком и его внешний вид.
//
// Роль здесь НЕ хранится - она читается из PlayerState. Раньше роль, камера и
// модель были перемешаны в одном классе, из-за чего игрок без роли сразу
// оказывался предметом. Теперь вид выводится из состояния:
//
//   Role.None   -> скин охотника + третье лицо (стартовое состояние)
//   Role.Hider  -> предмет + третье лицо + доступен выбор скина
//   Role.Hunter -> скин охотника + первое лицо + оружие
[RequireComponent(typeof(CharacterController))]
[RequireComponent(typeof(PlayerState))]
public class PlayerControllerNetCode : NetworkBehaviour
{
    [Header("Movement Settings")]
    public float moveSpeed = 5f;
    public float rotationSpeed = 10f;
    public float jumpHeight = 2f;
    public float gravity = -9.81f;

    [Header("Camera Settings")]
    public Transform cameraTransform;

    [Header("First Person (Hunter) Settings")]
    public float mouseSensitivity = 2f;
    [Tooltip("Пустой объект на уровне головы персонажа, который вращается вверх/вниз")]
    public Transform hunterCameraTarget;

    [Header("Cinemachine")]
    [SerializeField] private List<GameObject> virtualCameraObjects;
    [Tooltip("Камера от третьего лица: роль None и Hider")]
    [SerializeField] private CinemachineCamera thirdPersonCamera;
    [Tooltip("Камера от первого лица: только Hunter")]
    [SerializeField] private CinemachineCamera firstPersonCamera;

    [Header("Appearance Settings")]
    [Tooltip("Предметы, в которые превращается прячущийся")]
    public List<GameObject> models;
    [Tooltip("Модель охотника. Она же используется до раздачи ролей")]
    [SerializeField] private GameObject hunterModel;

    private CharacterController _controller;
    private PlayerState _state;
    private Vector3 _velocity;
    private float _cameraPitch;
    private bool _isNormalMovementMode = true;
    private bool _isMovementEnabled = true;

    // Наблюдатель перехватывает камеру, поэтому обычная логика вида отключается
    public bool IsSpectating { get; set; }

    public CinemachineCamera ThirdPersonCamera => thirdPersonCamera;

    private void Awake()
    {
        _controller = GetComponent<CharacterController>();
        _state = GetComponent<PlayerState>();

        SetVirtualCamerasActive(false);

        if (cameraTransform != null)
        {
            cameraTransform.gameObject.SetActive(false);

            if (cameraTransform.TryGetComponent(out AudioListener audioListener))
            {
                audioListener.enabled = false;
            }
        }
    }

    public override void OnNetworkSpawn()
    {
        if (IsOwner)
        {
            if (cameraTransform != null)
            {
                cameraTransform.gameObject.SetActive(true);

                if (cameraTransform.TryGetComponent(out AudioListener audioListener))
                {
                    audioListener.enabled = true;
                }
            }

            SetVirtualCamerasActive(true);
        }
        else
        {
            if (cameraTransform != null) cameraTransform.gameObject.SetActive(false);
            SetVirtualCamerasActive(false);
        }

        _state.RoleChanged += HandleRoleChanged;
        _state.AliveChanged += HandleAliveChanged;
        _state.ModelChanged += HandleModelChanged;

        ApplyRoleVisuals(_state.Role.Value);
    }

    public override void OnNetworkDespawn()
    {
        _state.RoleChanged -= HandleRoleChanged;
        _state.AliveChanged -= HandleAliveChanged;
        _state.ModelChanged -= HandleModelChanged;
    }

    private void SetVirtualCamerasActive(bool active)
    {
        if (virtualCameraObjects == null) return;

        foreach (GameObject virtualCameraObject in virtualCameraObjects)
        {
            if (virtualCameraObject != null) virtualCameraObject.SetActive(active);
        }
    }

    private void HandleRoleChanged(GameRole role) => ApplyRoleVisuals(role);
    private void HandleModelChanged(int index) => ApplyRoleVisuals(_state.Role.Value);

    private void HandleAliveChanged(bool alive)
    {
        ApplyRoleVisuals(_state.Role.Value);

        // Подстреленный больше не ходит и не мешает живым
        if (!alive)
        {
            _controller.enabled = false;
        }
        else if (IsOwner || IsServer)
        {
            _controller.enabled = true;
        }
    }

    private void Update()
    {
        if (!IsOwner || IsSpectating) return;

        // Мёртвый прячущийся не управляет телом - за него работает PlayerSpectator
        if (!_state.IsAlive.Value) return;

        if (Input.GetKeyDown(KeyCode.T))
        {
            _isMovementEnabled = !_isMovementEnabled;
            _controller.enabled = _isMovementEnabled;
        }

        if (_state.IsPlayableHider)
        {
            HandleAppearanceInput();
        }

        if (_state.IsHunter)
        {
            HandleMouseLook();
            HandleFirstPersonMovement();
            return;
        }

        if (!_isMovementEnabled) return;

        _isNormalMovementMode = !Input.GetKey(KeyCode.X);

        if (_isNormalMovementMode)
        {
            HandleThirdPersonMovement();
        }
        else
        {
            HandleObjectNudgeMovement();
        }
    }

    private void HandleThirdPersonMovement()
    {
        float moveX = Input.GetAxis("Horizontal");
        float moveZ = Input.GetAxis("Vertical");

        Vector3 moveDirection;

        if (cameraTransform != null)
        {
            Vector3 camForward = cameraTransform.forward;
            camForward.y = 0f;
            camForward.Normalize();

            Vector3 camRight = cameraTransform.right;
            camRight.y = 0f;
            camRight.Normalize();

            moveDirection = camForward * moveZ + camRight * moveX;
        }
        else
        {
            moveDirection = transform.forward * moveZ + transform.right * moveX;
        }

        ApplyGravityAndMove(moveDirection);

        if (moveDirection.sqrMagnitude > 0.001f)
        {
            Quaternion targetRotation = Quaternion.LookRotation(moveDirection);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, rotationSpeed * Time.deltaTime);
        }
    }

    // Движение без разворота корпуса: удобно точно подвинуть себя-предмет
    private void HandleObjectNudgeMovement()
    {
        float moveX = Input.GetAxis("Horizontal");
        float moveZ = Input.GetAxis("Vertical");

        Vector3 forward = transform.forward;
        forward.y = 0f;
        forward.Normalize();

        Vector3 right = transform.right;
        right.y = 0f;
        right.Normalize();

        ApplyGravityAndMove(forward * moveZ + right * moveX);
    }

    private void HandleFirstPersonMovement()
    {
        float moveX = Input.GetAxis("Horizontal");
        float moveZ = Input.GetAxis("Vertical");

        ApplyGravityAndMove(transform.forward * moveZ + transform.right * moveX);
    }

    private void ApplyGravityAndMove(Vector3 moveDirection)
    {
        if (_controller.isGrounded && _velocity.y < 0f)
        {
            _velocity.y = -2f;
        }

        _velocity.y += gravity * Time.deltaTime;

        if (Input.GetKeyDown(KeyCode.Space) && _controller.isGrounded)
        {
            _velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
        }

        Vector3 finalMove = moveDirection * moveSpeed;
        finalMove.y = _velocity.y;

        _controller.Move(finalMove * Time.deltaTime);
    }

    private void HandleAppearanceInput()
    {
        for (int i = 0; i < models.Count; i++)
        {
            if (Input.GetKeyDown(KeyCode.Alpha1 + i))
            {
                _state.RequestModelServerRpc(i);
            }
        }
    }

    private void HandleMouseLook()
    {
        float mouseX = Input.GetAxis("Mouse X") * mouseSensitivity;
        float mouseY = Input.GetAxis("Mouse Y") * mouseSensitivity;

        transform.Rotate(Vector3.up * mouseX);

        if (hunterCameraTarget != null)
        {
            _cameraPitch -= mouseY;
            _cameraPitch = Mathf.Clamp(_cameraPitch, -89f, 89f);
            hunterCameraTarget.localEulerAngles = new Vector3(_cameraPitch, 0f, 0f);
        }
    }

    [ClientRpc]
    public void TeleportClientRpc(Vector3 targetPosition)
    {
        bool controllerWasEnabled = _controller.enabled;

        _controller.enabled = false;
        transform.position = targetPosition;
        _controller.enabled = controllerWasEnabled;
    }

    // Главный переключатель вида. Вызывается при любом изменении состояния.
    private void ApplyRoleVisuals(GameRole role)
    {
        bool alive = _state.IsAlive.Value;
        // До раздачи ролей все выглядят охотником - это и есть стартовый вид
        bool looksLikeHunter = role != GameRole.Hider;

        if (hunterModel != null) hunterModel.SetActive(looksLikeHunter && alive);

        // -1 гасит все предметы разом
        ShowModel(looksLikeHunter || !alive ? -1 : _state.ModelIndex.Value);

        if (!IsOwner || IsSpectating) return;

        // От первого лица играет только охотник, всё остальное - от третьего
        bool firstPerson = role == GameRole.Hunter;

        if (thirdPersonCamera != null) thirdPersonCamera.Priority.Value = firstPerson ? 0 : 1;
        if (firstPersonCamera != null) firstPersonCamera.Priority.Value = firstPerson ? 1 : 0;
    }

    private void ShowModel(int index)
    {
        for (int i = 0; i < models.Count; i++)
        {
            if (models[i] != null) models[i].SetActive(i == index);
        }
    }
}
