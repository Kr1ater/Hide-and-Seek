using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

// Кладётся на объект со сценовым NetworkObject. При загрузке сцены сервер случайно
// выбирает numberToActivate объектов из objectPool и включает только их у всех клиентов,
// остальные остаются выключенными.
[RequireComponent(typeof(NetworkObject))]
public class RandomSceneObjectActivator : NetworkBehaviour
{
    [Header("Random Activation Settings")]
    [SerializeField] private List<GameObject> objectPool;
    [SerializeField] private int numberToActivate;

    private readonly NetworkList<bool> _isObjectActive = new NetworkList<bool>();

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            ChooseActiveObjects();
        }

        ApplyActiveStates();
    }

    private void ChooseActiveObjects()
    {
        // На случай повторного спавна объекта: иначе Add() дописал бы вторую пачку
        // значений поверх старых и длина списка перестала бы совпадать с objectPool.
        _isObjectActive.Clear();

        int countToActivate = Mathf.Clamp(numberToActivate, 0, objectPool.Count);

        var shuffledIndices = new List<int>(objectPool.Count);
        for (int i = 0; i < objectPool.Count; i++)
        {
            shuffledIndices.Add(i);
        }

        // Частичная тасовка Фишера-Йетса: перемешиваем только первые countToActivate элементов
        for (int i = 0; i < countToActivate; i++)
        {
            int swapIndex = Random.Range(i, shuffledIndices.Count);
            (shuffledIndices[i], shuffledIndices[swapIndex]) = (shuffledIndices[swapIndex], shuffledIndices[i]);
        }

        var activeIndices = new HashSet<int>(shuffledIndices.GetRange(0, countToActivate));

        for (int i = 0; i < objectPool.Count; i++)
        {
            _isObjectActive.Add(activeIndices.Contains(i));
        }
    }

    private void ApplyActiveStates()
    {
        if (_isObjectActive.Count != objectPool.Count)
        {
            Debug.LogError($"{nameof(RandomSceneObjectActivator)}: сервер прислал {_isObjectActive.Count} значений, а в objectPool {objectPool.Count} объектов. Список объектов должен быть одинаковым во всех копиях сцены.");
            return;
        }

        for (int i = 0; i < objectPool.Count; i++)
        {
            if (objectPool[i] != null)
            {
                objectPool[i].SetActive(_isObjectActive[i]);
            }
        }
    }
}
