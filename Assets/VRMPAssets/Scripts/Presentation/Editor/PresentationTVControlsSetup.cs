#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;
using TMPro;

namespace XRMultiplayer.Presentation.Editor
{
    /// <summary>
    /// Creates a world-space presentation page control strip for placement next to the TV.
    /// </summary>
    public static class PresentationTVControlsSetup
    {
        static readonly Color PanelBg = new Color(0.08f, 0.09f, 0.12f, 0.92f);
        static readonly Color ButtonColor = new Color(0.125f, 0.588f, 0.953f, 1f);
        static readonly Color ButtonHighlight = new Color(0.2f, 0.65f, 1f, 1f);
        static readonly Color StopColor = new Color(0.75f, 0.22f, 0.22f, 1f);

        [MenuItem("XR Multiplayer/Create TV Presentation Controls")]
        public static void CreateTVControls()
        {
            var existing = Object.FindFirstObjectByType<PresentationTVControlsUI>();
            if (existing != null)
            {
                Selection.activeGameObject = existing.gameObject;
                Debug.Log("[PresentationTVControls] Already exists — selected existing object.");
                return;
            }

            Sprite themeSprite = AssetDatabase.LoadAssetAtPath<Sprite>(
                "Assets/VRMPAssets/Textures/UI/Round Radius 10.png");

            Transform parent = null;
            var presentationSystem = Object.FindFirstObjectByType<PresentationNetworkManager>();
            if (presentationSystem != null)
                parent = presentationSystem.transform;

            var canvasGO = new GameObject("PresentationTVControls_Canvas");
            if (parent != null)
            {
                Undo.SetTransformParent(canvasGO.transform, parent, "Parent TV Controls");
                canvasGO.transform.localPosition = new Vector3(1.2f, 0.5f, 0f);
                canvasGO.transform.localRotation = Quaternion.identity;
            }
            else
            {
                canvasGO.transform.position = new Vector3(0f, 1.5f, 2f);
                canvasGO.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
            }

            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvasGO.AddComponent<CanvasScaler>();
            canvasGO.AddComponent<GraphicRaycaster>();
            canvasGO.AddComponent<TrackedDeviceGraphicRaycaster>();

            var canvasRect = canvasGO.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(520f, 140f);
            canvasRect.localScale = Vector3.one * 0.001f;

            var col = canvasGO.AddComponent<BoxCollider>();
            col.size = new Vector3(520f, 140f, 1f);
            col.isTrigger = true;

            var panel = CreatePanel(canvasGO.transform, "ControlsPanel", themeSprite, PanelBg);
            var panelRT = panel.GetComponent<RectTransform>();
            panelRT.anchorMin = Vector2.zero;
            panelRT.anchorMax = Vector2.one;
            panelRT.offsetMin = Vector2.zero;
            panelRT.offsetMax = Vector2.zero;

            var pageLabel = CreateText(panel.transform, "PageLabel", "—", 32, Color.white);
            var labelRT = pageLabel.GetComponent<RectTransform>();
            labelRT.anchorMin = new Vector2(0.05f, 0.55f);
            labelRT.anchorMax = new Vector2(0.95f, 0.95f);
            labelRT.offsetMin = Vector2.zero;
            labelRT.offsetMax = Vector2.zero;

            var prevBtn = CreateButton(panel.transform, "PreviousButton", "◀  Prev", themeSprite, ButtonColor);
            var prevRT = prevBtn.GetComponent<RectTransform>();
            prevRT.anchorMin = new Vector2(0.04f, 0.08f);
            prevRT.anchorMax = new Vector2(0.31f, 0.48f);
            prevRT.offsetMin = Vector2.zero;
            prevRT.offsetMax = Vector2.zero;

            var nextBtn = CreateButton(panel.transform, "NextButton", "Next  ▶", themeSprite, ButtonColor);
            var nextRT = nextBtn.GetComponent<RectTransform>();
            nextRT.anchorMin = new Vector2(0.36f, 0.08f);
            nextRT.anchorMax = new Vector2(0.63f, 0.48f);
            nextRT.offsetMin = Vector2.zero;
            nextRT.offsetMax = Vector2.zero;

            var stopBtn = CreateButton(panel.transform, "StopButton", "Stop", themeSprite, StopColor);
            var stopRT = stopBtn.GetComponent<RectTransform>();
            stopRT.anchorMin = new Vector2(0.68f, 0.08f);
            stopRT.anchorMax = new Vector2(0.96f, 0.48f);
            stopRT.offsetMin = Vector2.zero;
            stopRT.offsetMax = Vector2.zero;

            var controls = canvasGO.AddComponent<PresentationTVControlsUI>();
            var so = new SerializedObject(controls);
            so.FindProperty("controlsRoot").objectReferenceValue = panel;
            so.FindProperty("previousButton").objectReferenceValue = prevBtn.GetComponent<Button>();
            so.FindProperty("nextButton").objectReferenceValue = nextBtn.GetComponent<Button>();
            so.FindProperty("stopButton").objectReferenceValue = stopBtn.GetComponent<Button>();
            so.FindProperty("pageLabel").objectReferenceValue = pageLabel.GetComponent<TextMeshProUGUI>();
            if (presentationSystem != null)
                so.FindProperty("networkManager").objectReferenceValue = presentationSystem;
            so.ApplyModifiedProperties();

            Undo.RegisterCreatedObjectUndo(canvasGO, "Create TV Presentation Controls");
            Selection.activeGameObject = canvasGO;

            Debug.Log(
                "[PresentationTVControls] Created PresentationTVControls_Canvas.\n" +
                "Move/rotate it next to your TV screen in the Scene view, then enter Play Mode to test.");
        }

        static GameObject CreatePanel(Transform parent, string name, Sprite sprite, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = color;
            if (sprite != null)
            {
                img.sprite = sprite;
                img.type = Image.Type.Sliced;
            }
            return go;
        }

        static GameObject CreateText(Transform parent, string name, string text, int fontSize, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var tmp = go.GetComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.color = color;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.fontStyle = FontStyles.Bold;
            return go;
        }

        static GameObject CreateButton(Transform parent, string name, string label, Sprite sprite, Color normalColor)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = normalColor;
            if (sprite != null)
            {
                img.sprite = sprite;
                img.type = Image.Type.Sliced;
            }

            var btn = go.GetComponent<Button>();
            var colors = btn.colors;
            colors.normalColor = normalColor;
            colors.highlightedColor = normalColor == StopColor
                ? new Color(0.9f, 0.3f, 0.3f, 1f)
                : ButtonHighlight;
            colors.pressedColor = normalColor * 0.85f;
            btn.colors = colors;

            var textGO = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            textGO.transform.SetParent(go.transform, false);
            var textRT = textGO.GetComponent<RectTransform>();
            textRT.anchorMin = Vector2.zero;
            textRT.anchorMax = Vector2.one;
            textRT.offsetMin = new Vector2(4f, 4f);
            textRT.offsetMax = new Vector2(-4f, -4f);
            var tmp = textGO.GetComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 22;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;

            return go;
        }
    }
}
#endif
