using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Заставка студии в сцене Bootstrap: сверху бьёт молния, вспышка -
// и проявляется название, которое затем плавно отдаляется.
//
// Весь интерфейс создаётся кодом, в редакторе достаточно повесить скрипт
// на любой объект сцены Bootstrap.
//
// ВАЖНО про уборку: Canvas создаётся КОРНЕВЫМ объектом сцены, а не ребёнком
// этого скрипта. Если повесить скрипт на NetworkManager (он помечен
// DontDestroyOnLoad), дочерний Canvas уехал бы вместе с ним в следующие сцены
// и навсегда закрыл бы игру чёрным фоном. Плюс в конце всё сносится явно.
public class StudioSplash : MonoBehaviour
{
    [Header("Текст")]
    [SerializeField] private string studioName = "ALTSOURCE";
    [Tooltip("Оставь пустым - возьмётся шрифт TMP по умолчанию")]
    [SerializeField] private TMP_FontAsset fontAsset;
    [SerializeField] private float fontSize = 110f;
    [SerializeField] private float characterSpacing = 18f;
    [SerializeField] private Color textColor = Color.white;

    [Header("Отдаление текста")]
    [Tooltip("Масштаб в момент удара молнии")]
    [SerializeField] private float zoomFrom = 1.35f;
    [Tooltip("Масштаб к концу заставки. Меньше - сильнее отдаляется")]
    [SerializeField] private float zoomTo = 0.85f;

    [Header("Фон")]
    [SerializeField] private Color backgroundColor = new Color(0.02f, 0.02f, 0.04f, 1f);

    [Header("Молния")]
    [SerializeField] private Color boltColor = new Color(0.85f, 0.93f, 1f, 1f);
    [Tooltip("Из скольких отрезков состоит молния. Больше - изломаннее")]
    [SerializeField] private int boltSegments = 14;
    [Tooltip("Насколько сильно молнию ведёт в стороны, в пикселях")]
    [SerializeField] private float boltJaggedness = 90f;
    [SerializeField] private float boltThickness = 14f;
    [Tooltip("Во сколько раз свечение шире самой молнии")]
    [SerializeField] private float glowWidthMultiplier = 3.5f;

    [Header("Вспышка")]
    [SerializeField] private Color flashColor = Color.white;

    [Header("Тайминги, сек")]
    [SerializeField] private float initialDelay = 0.45f;
    [Tooltip("За сколько молния прочерчивается сверху вниз")]
    [SerializeField] private float strikeDuration = 0.35f;
    [Tooltip("Сколько молния держится и мерцает после удара")]
    [SerializeField] private float boltLingerDuration = 0.35f;
    [SerializeField] private float flashFadeDuration = 0.5f;
    [Tooltip("Сколько название висит на экране после появления")]
    [SerializeField] private float holdDuration = 1.4f;
    [SerializeField] private float fadeOutDuration = 0.6f;

    [Header("Звук (необязательно)")]
    [SerializeField] private AudioClip thunderClip;
    [Range(0f, 1f)]
    [SerializeField] private float thunderVolume = 0.7f;

    [Header("Управление")]
    [SerializeField] private bool skippable = true;

    private GameObject _canvasObject;
    private RectTransform _canvasRect;
    private Image _flash;
    private TextMeshProUGUI _text;
    private UILightningBolt _bolt;
    private UILightningBolt _boltGlow;
    private AudioSource _audioSource;

    private readonly List<Vector2> _boltPoints = new List<Vector2>();
    private bool _skipRequested;
    private bool _isPlaying;

    private void Update()
    {
        if (_isPlaying && skippable && Input.anyKeyDown) _skipRequested = true;
    }

    // Вызывается загрузчиком сцены и держит его, пока заставка не отыграет
    public IEnumerator Play()
    {
        _isPlaying = true;
        BuildInterface();

        // Ждём кадр: до первой раскладки размеры Canvas ещё нулевые,
        // и молнию некуда было бы вести
        yield return null;

        Vector2 canvasSize = _canvasRect.rect.size;
        Vector2 strikeTarget = new Vector2(0f, fontSize * 0.35f);
        Vector2 strikeStart = new Vector2(strikeTarget.x, canvasSize.y * 0.5f);

        yield return WaitOrSkip(initialDelay);

        if (_audioSource != null) _audioSource.Play();

        yield return StrikeBolt(strikeStart, strikeTarget);

        // Момент удара: вспышка на весь экран, и в ней проступает название
        SetAlpha(_flash, 1f);
        _text.alpha = 1f;
        SetTextScale(zoomFrom);

        // Молния гаснет не мгновенно, а дотлевает и мерцает - иначе её просто
        // не успеваешь заметить за вспышкой
        StartCoroutine(FadeBoltOut(strikeStart, strikeTarget));

        yield return FlashAndSettle();
        yield return HoldAndRecede();
        yield return FadeOut();

        Teardown();
    }

    // ---------- Этапы ----------

    private IEnumerator StrikeBolt(Vector2 start, Vector2 target)
    {
        float elapsed = 0f;

        while (elapsed < strikeDuration)
        {
            if (_skipRequested) yield break;

            elapsed += Time.deltaTime;

            // Форму пересчитываем каждый кадр - молния "живёт" и дрожит
            RebuildBolt(start, target);
            float reveal = Mathf.Clamp01(elapsed / strikeDuration);
            _bolt.Reveal = reveal;
            _boltGlow.Reveal = reveal;

            yield return null;
        }
    }

    private IEnumerator FadeBoltOut(Vector2 start, Vector2 target)
    {
        float elapsed = 0f;

        while (elapsed < boltLingerDuration)
        {
            // Заставку могли пропустить, и Canvas уже уничтожен
            if (_bolt == null || _boltGlow == null) yield break;

            elapsed += Time.deltaTime;
            float remaining = 1f - Mathf.Clamp01(elapsed / boltLingerDuration);

            // Разряд ещё пару раз дёргается, прежде чем погаснуть
            RebuildBolt(start, target);
            SetAlpha(_bolt, remaining * (Random.value < 0.3f ? 0.4f : 1f));
            SetAlpha(_boltGlow, remaining * 0.35f);

            yield return null;
        }

        if (_bolt == null || _boltGlow == null) yield break;

        SetAlpha(_bolt, 0f);
        SetAlpha(_boltGlow, 0f);
    }

    private IEnumerator FlashAndSettle()
    {
        float elapsed = 0f;

        while (elapsed < flashFadeDuration)
        {
            if (_skipRequested) break;

            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / flashFadeDuration);

            SetAlpha(_flash, 1f - t);

            // В первые мгновения текст подрагивает, будто по нему идёт разряд
            _text.alpha = t < 0.45f && Random.value < 0.35f ? 0.35f : 1f;

            yield return null;
        }

        SetAlpha(_flash, 0f);
        _text.alpha = 1f;
    }

    // Название плавно отдаляется, пока висит на экране
    private IEnumerator HoldAndRecede()
    {
        float elapsed = 0f;

        while (elapsed < holdDuration)
        {
            if (_skipRequested) break;

            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / holdDuration);

            // SmoothStep: быстрее в начале, мягко тормозит к концу
            SetTextScale(Mathf.SmoothStep(zoomFrom, zoomTo, t));

            yield return null;
        }

        SetTextScale(zoomTo);
    }

    private IEnumerator FadeOut()
    {
        float elapsed = 0f;

        while (elapsed < fadeOutDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / fadeOutDuration);

            _text.alpha = 1f - t;
            // Отдаление продолжается и во время затухания
            SetTextScale(Mathf.Lerp(zoomTo, zoomTo * 0.92f, t));

            yield return null;
        }
    }

    private IEnumerator WaitOrSkip(float seconds)
    {
        float elapsed = 0f;

        while (elapsed < seconds)
        {
            if (_skipRequested) yield break;

            elapsed += Time.deltaTime;
            yield return null;
        }
    }

    // ---------- Молния ----------

    private void RebuildBolt(Vector2 start, Vector2 target)
    {
        GenerateBoltPath(start, target);
        _boltGlow.SetPoints(_boltPoints, boltThickness * glowWidthMultiplier);
        _bolt.SetPoints(_boltPoints, boltThickness);
    }

    // Ломаная строится смещением точек в сторону от прямой.
    // У концов смещение гасится синусом, чтобы молния начиналась и
    // заканчивалась ровно там, где надо.
    private void GenerateBoltPath(Vector2 start, Vector2 end)
    {
        _boltPoints.Clear();
        _boltPoints.Add(start);

        Vector2 direction = (end - start).normalized;
        Vector2 sideways = new Vector2(-direction.y, direction.x);

        for (int i = 1; i < boltSegments; i++)
        {
            float t = i / (float)boltSegments;
            float falloff = Mathf.Sin(t * Mathf.PI);

            Vector2 point = Vector2.Lerp(start, end, t)
                            + sideways * Random.Range(-boltJaggedness, boltJaggedness) * falloff;

            _boltPoints.Add(point);
        }

        _boltPoints.Add(end);
    }

    // ---------- Построение и уборка ----------

    private void BuildInterface()
    {
        // Корневой объект сцены, а НЕ ребёнок this: иначе заставка уехала бы
        // в следующие сцены вместе с DontDestroyOnLoad-объектом
        _canvasObject = new GameObject("StudioSplashCanvas", typeof(Canvas), typeof(CanvasScaler));

        var canvas = _canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 32000;

        var scaler = _canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        _canvasRect = (RectTransform)_canvasObject.transform;

        CreateFullScreenImage(_canvasRect, "Background", backgroundColor);

        // Сначала широкое свечение, поверх него - яркое ядро молнии
        _boltGlow = CreateBolt("BoltGlow", new Color(boltColor.r, boltColor.g, boltColor.b, 0.35f));
        _bolt = CreateBolt("Bolt", boltColor);

        CreateText();

        // Вспышка последняя - значит рисуется поверх всего
        _flash = CreateFullScreenImage(_canvasRect, "Flash", flashColor);
        SetAlpha(_flash, 0f);

        if (thunderClip != null)
        {
            _audioSource = _canvasObject.AddComponent<AudioSource>();
            _audioSource.clip = thunderClip;
            _audioSource.volume = thunderVolume;
            _audioSource.playOnAwake = false;
        }
    }

    private UILightningBolt CreateBolt(string name, Color color)
    {
        // CanvasRenderer указываем явно: без него Graphic ничего не нарисует
        var boltObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(UILightningBolt));

        var rect = (RectTransform)boltObject.transform;
        rect.SetParent(_canvasRect, false);
        Stretch(rect);

        var bolt = boltObject.GetComponent<UILightningBolt>();
        bolt.color = color;
        bolt.raycastTarget = false;
        bolt.Reveal = 0f;

        return bolt;
    }

    private void CreateText()
    {
        var textObject = new GameObject("StudioName", typeof(RectTransform));
        var textRect = (RectTransform)textObject.transform;
        textRect.SetParent(_canvasRect, false);
        textRect.anchorMin = new Vector2(0.5f, 0.5f);
        textRect.anchorMax = new Vector2(0.5f, 0.5f);
        textRect.pivot = new Vector2(0.5f, 0.5f);
        textRect.anchoredPosition = Vector2.zero;
        textRect.sizeDelta = new Vector2(1600f, 300f);

        _text = textObject.AddComponent<TextMeshProUGUI>();
        if (fontAsset != null) _text.font = fontAsset;

        _text.SetText(studioName);
        _text.fontSize = fontSize;
        _text.characterSpacing = characterSpacing;
        _text.color = textColor;
        _text.alignment = TextAlignmentOptions.Center;
        _text.raycastTarget = false;
        // До удара молнии названия не видно
        _text.alpha = 0f;
    }

    // Явная уборка: заставка не должна пережить переход в лобби ни при каких условиях
    private void Teardown()
    {
        _isPlaying = false;

        // Гасим побочные корутины (мерцание молнии) до сноса Canvas,
        // иначе они обратятся к уничтоженным объектам.
        // Саму Play() это не тронет - её крутит BootstrapSceneLoader.
        StopAllCoroutines();

        if (_canvasObject != null) Destroy(_canvasObject);

        _bolt = null;
        _boltGlow = null;
        _flash = null;
        _text = null;

        // Скрипт мог быть повешен на DontDestroyOnLoad-объект и пережить сцену,
        // поэтому выключаем его, чтобы Update больше не работал
        enabled = false;
    }

    private void SetTextScale(float scale)
    {
        _text.rectTransform.localScale = Vector3.one * scale;
    }

    private static Image CreateFullScreenImage(RectTransform parent, string name, Color color)
    {
        var imageObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        var rect = (RectTransform)imageObject.transform;
        rect.SetParent(parent, false);
        Stretch(rect);

        var image = imageObject.GetComponent<Image>();
        image.color = color;
        image.raycastTarget = false;

        return image;
    }

    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    private static void SetAlpha(Graphic graphic, float alpha)
    {
        Color color = graphic.color;
        graphic.color = new Color(color.r, color.g, color.b, alpha);
    }
}
