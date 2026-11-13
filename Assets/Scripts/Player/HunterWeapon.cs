using Unity.Netcode;
using UnityEngine;

// Оружие охотника. Патроны и попадания считает СЕРВЕР: клиент лишь сообщает,
// куда он целился. Иначе любой игрок смог бы объявить себя победителем.
//
// Боезапас выдаётся один раз при раздаче ролей: n патронов на каждого
// прячущегося. Промах тратит патрон, одно попадание убивает.
[RequireComponent(typeof(PlayerState))]
public class HunterWeapon : NetworkBehaviour
{
    [Header("Оружие")]
    [Tooltip("Модель оружия в руках. Включается вместе с ролью охотника")]
    [SerializeField] private GameObject weaponModel;
    [Tooltip("Откуда вылетает выстрел. Обычно дуло. Если пусто - берётся камера")]
    [SerializeField] private Transform muzzlePoint;

    [Header("Настройки стрельбы")]
    [SerializeField] private float range = 100f;
    [SerializeField] private float shotCooldown = 0.35f;
    [Tooltip("Слои, по которым стреляем. Должны включать игроков и стены")]
    [SerializeField] private LayerMask hittableLayers = ~0;

    [Header("Эффекты (необязательно)")]
    [SerializeField] private ParticleSystem muzzleFlash;
    [SerializeField] private AudioSource shotSound;

    public NetworkVariable<int> Ammo = new NetworkVariable<int>(0);

    private PlayerState _state;
    private Camera _aimCamera;
    private float _nextShotTime;

    private void Awake()
    {
        _state = GetComponent<PlayerState>();
        if (weaponModel != null) weaponModel.SetActive(false);
    }

    public override void OnNetworkSpawn()
    {
        _state.RoleChanged += HandleRoleChanged;
        HandleRoleChanged(_state.Role.Value);
    }

    public override void OnNetworkDespawn()
    {
        _state.RoleChanged -= HandleRoleChanged;
    }

    private void HandleRoleChanged(GameRole role)
    {
        // Оружие видно всем, а не только владельцу: прячущиеся должны видеть,
        // что к ним идёт вооружённый охотник
        if (weaponModel != null) weaponModel.SetActive(role == GameRole.Hunter);
    }

    private void Update()
    {
        if (!IsOwner || !_state.IsHunter || !_state.IsAlive.Value) return;
        if (Ammo.Value <= 0) return;
        if (Time.time < _nextShotTime) return;
        if (!Input.GetMouseButtonDown(0)) return;

        _nextShotTime = Time.time + shotCooldown;

        Transform origin = GetAimOrigin();
        if (origin == null) return;

        // Отправляем именно луч прицеливания: сервер сам решит, попали ли
        FireServerRpc(origin.position, origin.forward);
    }

    private Transform GetAimOrigin()
    {
        if (muzzlePoint != null) return muzzlePoint;

        if (_aimCamera == null) _aimCamera = Camera.main;
        return _aimCamera != null ? _aimCamera.transform : null;
    }

    [ServerRpc]
    private void FireServerRpc(Vector3 origin, Vector3 direction)
    {
        // Повторная проверка на сервере: клиент мог прислать выстрел без права на него
        if (!_state.IsHunter || !_state.IsAlive.Value || Ammo.Value <= 0) return;

        // ПОРЯДОК ВАЖЕН: сначала попадание, потом списание патрона.
        // Последним патроном можно убить последнего прячущегося - тогда охотник
        // должен победить. Если списать патрон первым, GameManager увидел бы
        // "патроны кончились" раньше, чем "все найдены", и засчитал бы поражение.
        if (Physics.Raycast(origin, direction, out RaycastHit hit, range, hittableLayers))
        {
            // GetComponentInParent: коллайдер обычно висит не на корне игрока
            PlayerState target = hit.collider.GetComponentInParent<PlayerState>();

            if (target != null && target.IsPlayableHider)
            {
                target.ServerSetAlive(false);
            }
        }

        Ammo.Value--;

        PlayFireEffectsClientRpc();
    }

    [ClientRpc]
    private void PlayFireEffectsClientRpc()
    {
        if (muzzleFlash != null) muzzleFlash.Play();
        if (shotSound != null) shotSound.Play();
    }

    // ---------- Серверное API ----------

    public void ServerGrantAmmo(int amount)
    {
        if (!IsServer) return;
        Ammo.Value = Mathf.Max(0, amount);
    }
}
