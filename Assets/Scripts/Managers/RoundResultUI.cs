using UnityEngine;

// Экран итога раунда. Сами таблички делаются в Canvas вручную -
// сюда достаточно перетащить два объекта.
//
// Скрипт ничего не решает: GameManager сообщает, кто победил, и каждый
// клиент сам сравнивает это со своей ролью.
public class RoundResultUI : MonoBehaviour
{
    [Header("Таблички")]
    [SerializeField] private GameObject victoryPanel;
    [SerializeField] private GameObject defeatPanel;

    private void Awake()
    {
        Hide();
    }

    public void Show(bool localPlayerWon)
    {
        if (victoryPanel != null) victoryPanel.SetActive(localPlayerWon);
        if (defeatPanel != null) defeatPanel.SetActive(!localPlayerWon);
    }

    public void Hide()
    {
        if (victoryPanel != null) victoryPanel.SetActive(false);
        if (defeatPanel != null) defeatPanel.SetActive(false);
    }
}
