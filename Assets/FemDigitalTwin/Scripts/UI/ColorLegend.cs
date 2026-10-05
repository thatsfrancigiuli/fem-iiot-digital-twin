using System.Collections.Generic;
using System.Globalization;
using FemDigitalTwin.Core;
using FemDigitalTwin.Rendering;
using UnityEngine;
using UnityEngine.UI;

namespace FemDigitalTwin.UI
{
    /// <summary>
    /// On-screen colour legend for the FEM point cloud.
    ///
    /// The legend is sampled at a fixed number of discrete steps over the (locked) colour scale
    /// of the imported field. Labels are linear between min and max; colours use exactly the
    /// same mapping as the renderer (ColorMapping), including exponent and offset, so the legend
    /// always matches the geometry.
    /// </summary>
    public class ColorLegend : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private PointCloudGenerator source;
        [SerializeField] private RectTransform container;
        [Tooltip("Optional prefab with an Image and a child Text. If empty, a default item is created.")]
        [SerializeField] private GameObject itemPrefab;
        [SerializeField] private Text titleText;

        [Header("Content")]
        [SerializeField] private string title = "Von Mises";
        [SerializeField] private string unit = "MPa";
        [Tooltip("Multiplies the label values only (e.g. 1000 to show very small strains). Does not change the data.")]
        [SerializeField] private float labelScale = 1f;
        [SerializeField] private string numberFormat = "F2";
        [SerializeField, Range(3, 20)] private int steps = 8;

        [Header("Layout and style")]
        [SerializeField] private float itemHeight = 40f;
        [SerializeField] private float itemSpacing = 2f;
        [SerializeField] private Font font;
        [SerializeField] private int fontSize = 14;
        [SerializeField] private Color textColor = Color.white;

        private readonly List<GameObject> items = new List<GameObject>();

        private void OnEnable()
        {
            if (source == null) source = FindObjectOfType<PointCloudGenerator>();
            if (source != null) source.FieldChanged += Rebuild;
        }

        private void OnDisable()
        {
            if (source != null) source.FieldChanged -= Rebuild;
        }

        private void Start()
        {
            if (source != null && source.IsLoaded) Rebuild();
        }

        public void SetVisible(bool visible)
        {
            if (container != null) container.gameObject.SetActive(visible);
        }

        public void Rebuild()
        {
            if (source == null || !source.IsLoaded || container == null) return;
            Clear();

            ColorScale s = source.Scale;
            var inv = CultureInfo.InvariantCulture;
            if (titleText != null)
                titleText.text = $"{title} [{unit}]\n({(s.Min * labelScale).ToString(numberFormat, inv)} – " +
                                 $"{(s.Max * labelScale).ToString(numberFormat, inv)})";

            for (int i = 0; i < steps; i++)
            {
                float t = (float)i / (steps - 1);
                float value = Mathf.Lerp(s.Min, s.Max, t);
                GameObject item = itemPrefab != null ? Instantiate(itemPrefab, container) : CreateDefaultItem();

                var rect = item.GetComponent<RectTransform>();
                rect.anchorMin = new Vector2(0f, 1f);
                rect.anchorMax = new Vector2(1f, 1f);
                rect.pivot = new Vector2(0f, 1f);
                rect.anchoredPosition = new Vector2(0f, -i * (itemHeight + itemSpacing));
                rect.sizeDelta = new Vector2(0f, itemHeight);

                var image = item.GetComponent<Image>();
                if (image != null) image.color = s.ColorOf(value);
                var label = item.GetComponentInChildren<Text>();
                if (label != null) label.text = (value * labelScale).ToString(numberFormat, inv);

                items.Add(item);
            }
        }

        private GameObject CreateDefaultItem()
        {
            var item = new GameObject("LegendItem", typeof(RectTransform), typeof(Image));
            item.transform.SetParent(container, false);

            var textGo = new GameObject("Value", typeof(RectTransform), typeof(Text));
            textGo.transform.SetParent(item.transform, false);
            var text = textGo.GetComponent<Text>();
            text.font = font != null ? font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = fontSize;
            text.color = textColor;
            text.alignment = TextAnchor.MiddleLeft;

            var r = textGo.GetComponent<RectTransform>();
            r.anchorMin = new Vector2(0.35f, 0f);
            r.anchorMax = new Vector2(1f, 1f);
            r.offsetMin = new Vector2(5f, 0f);
            r.offsetMax = new Vector2(-5f, 0f);
            return item;
        }

        private void Clear()
        {
            foreach (var go in items) if (go != null) Destroy(go);
            items.Clear();
        }
    }
}
