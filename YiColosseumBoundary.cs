using System;
using System.Collections.Generic;
using UnityEngine;

namespace Yi_in_Coloseum
{
    /// <summary>
    /// Notice only. Never adds a collider, disables a door, or blocks a transition.
    /// The notice renders on an independent GameObject, isolated from Yi's HUD.
    /// </summary>
    public sealed class YiColosseumBoundary : MonoBehaviour
    {
        private const float EntryGraceSeconds = 5f;
        private const float ScanIntervalSeconds = 1f;
        private const float ExitDetectionRadius = 12f;
        private const float PreviewSeconds = 8f;

        private readonly List<TransitionPoint> externalExits = new List<TransitionPoint>();
        private string currentScene = "";
        private float enteredAt;
        private float nextScanAt;
        private float previewUntil = -1f;
        private bool dismissed;
        private bool closeToExit;
        private bool loggedVisible;
        private YiExitNoticeCanvas noticeCanvas;
        private GameObject noticeObject;

        private static bool IsColosseum(string scene)
        {
            return !string.IsNullOrEmpty(scene) &&
                   scene.StartsWith("Room_Colosseum", StringComparison.Ordinal);
        }

        private void Awake()
        {
            // A dedicated GO gives the panel its OWN OnGUI callback, avoiding
            // the scaled character/HUD GUI state that mispositioned old notices.
            noticeObject = new GameObject("Yi Centered Colosseum Notice");
            noticeCanvas = noticeObject.AddComponent<YiExitNoticeCanvas>();
            noticeCanvas.Initialize(this);
        }

        internal bool ShouldShowNotice
        {
            get
            {
                bool preview = Time.unscaledTime < previewUntil;
                return (preview || (IsColosseum(currentScene) && closeToExit && !dismissed &&
                        Time.unscaledTime - enteredAt >= EntryGraceSeconds)) &&
                        Time.timeScale > 0.01f;
            }
        }

        private void Update()
        {
            // Ctrl+Shift+P: preview the center panel from ANY room, for 8 s.
            // This verifies rendering independently of door detection.
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (ctrl && shift && Input.GetKeyDown(KeyCode.P))
            {
                previewUntil = Time.unscaledTime + PreviewSeconds;
                loggedVisible = false;
                Debug.Log("[Yi Exit] NOTICE_PREVIEW requested");
            }

            string scene = Application.loadedLevelName;
            if (currentScene != scene)
            {
                currentScene = scene;
                enteredAt = Time.unscaledTime;
                nextScanAt = 0f;
                externalExits.Clear();
                dismissed = false;
                loggedVisible = false;
                closeToExit = false;
                Debug.Log("[Yi Exit] SCENE=" + scene + "; ENTRY_GRACE=5; NO_PHYSICAL_GATES");
            }
            closeToExit = false;
            if (!IsColosseum(currentScene)) return;

            // No physical or transition colliders are ever touched.
            if (Time.unscaledTime < enteredAt + EntryGraceSeconds) return;
            if (Time.unscaledTime >= nextScanAt)
            {
                nextScanAt = Time.unscaledTime + ScanIntervalSeconds;
                externalExits.Clear();
                TransitionPoint[] transitions = FindObjectsOfType<TransitionPoint>();
                foreach (TransitionPoint point in transitions)
                {
                    if (point == null || string.IsNullOrEmpty(point.targetScene)) continue;
                    if (!IsColosseum(point.targetScene)) externalExits.Add(point);
                }
            }
            float nearestSq = ExitDetectionRadius * ExitDetectionRadius;
            foreach (TransitionPoint point in externalExits)
            {
                if (point == null) continue;
                float sq = (point.transform.position - transform.position).sqrMagnitude;
                if (sq < nearestSq) { nearestSq = sq; closeToExit = true; }
            }
            if (closeToExit && !loggedVisible)
            {
                loggedVisible = true;
                Debug.Log("[Yi Exit] EXTERNAL_EXIT_NEAR; notice shown (doors NEVER disabled)");
            }
            if (Input.GetKeyDown(KeyCode.L))
            {
                dismissed = true;
                previewUntil = -1f;
                Debug.Log("[Yi Exit] NOTICE_DISMISSED_BY_L");
            }
        }

        private void OnDisable()
        {
            if (noticeObject != null) noticeObject.SetActive(false);
        }

        private void OnEnable()
        {
            if (noticeObject != null) noticeObject.SetActive(true);
        }

        private void OnDestroy()
        {
            if (noticeObject != null) Destroy(noticeObject);
        }
    }

    /// <summary>
    /// Dedicated, centered IMGUI overlay. This is intentionally NOT a child of
    /// the hero's HUD object, and uses untransformed pixel screen coordinates.
    /// </summary>
    public sealed class YiExitNoticeCanvas : MonoBehaviour
    {
        private YiColosseumBoundary owner;
        private GUIStyle heading, body, footer;
        private bool lastShown;
        private int lastWidth = -1, lastHeight = -1;

        internal void Initialize(YiColosseumBoundary boundary) { owner = boundary; }

        private void OnGUI()
        {
            bool show = owner != null && owner.ShouldShowNotice;
            if (!show) { lastShown = false; return; }

            Matrix4x4 savedMatrix = GUI.matrix;
            Color savedColor = GUI.color;
            int savedDepth = GUI.depth;
            try
            {
                GUI.matrix = Matrix4x4.identity;
                GUI.depth = -16000;

                float sw = Screen.width;
                float sh = Screen.height;
                float w = Mathf.Min(sw - 24f, sw * 0.84f);
                float h = Mathf.Min(sh - 24f, sh * 0.56f);
                float x = (sw - w) * 0.5f;
                float y = (sh - h) * 0.5f;
                if (w <= 100f || h <= 100f) return;

                if (!lastShown || lastWidth != Screen.width || lastHeight != Screen.height)
                {
                    lastShown = true;
                    lastWidth = Screen.width;
                    lastHeight = Screen.height;
                    Debug.Log("[Yi Exit] CENTERED_NOTICE_RECT screen=" + sw + "x" + sh +
                        " rect=" + x + "," + y + "," + w + "," + h);
                }

                // Dim background so it is unmistakably a centered notice, not
                // a second HUD label in the bottom corner.
                GUI.color = new Color(0f, 0f, 0f, 0.27f);
                GUI.DrawTexture(new Rect(0f, 0f, sw, sh), Texture2D.whiteTexture);
                GUI.color = new Color(0.025f, 0.055f, 0.08f, 0.98f);
                GUI.DrawTexture(new Rect(x, y, w, h), Texture2D.whiteTexture);

                float scale = Mathf.Clamp(sh / 1080f, 0.65f, 1.6f);
                heading = PrepareStyle(heading, Mathf.RoundToInt(49f * scale),
                    FontStyle.Bold, new Color(1f, 0.91f, 0.64f));
                body = PrepareStyle(body, Mathf.RoundToInt(30f * scale),
                    FontStyle.Normal, Color.white);
                footer = PrepareStyle(footer, Mathf.RoundToInt(27f * scale),
                    FontStyle.Bold, new Color(0.67f, 1f, 0.8f));

                GUI.color = new Color(0.64f, 1f, 0.76f, 1f);
                GUI.DrawTexture(new Rect(x + 18f*scale, y + 15f*scale,
                    w - 36f*scale, 5f*scale), Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Label(new Rect(x+w*0.04f, y+h*0.10f, w*0.92f, h*0.21f),
                    "BEYOND THE COLOSSEUM", heading);
                GUI.Label(new Rect(x+w*0.09f, y+h*0.35f, w*0.82f, h*0.29f),
                    "Yi was designed for the Colosseum of Fools. Other areas of Hallownest may have unsupported combat, animations, or progression.", body);
                GUI.Label(new Rect(x+w*0.06f, y+h*0.72f, w*0.88f, h*0.19f),
                    "TURN BACK  |  PRESS L TO DISMISS  |  EXIT IS ALWAYS PASSABLE", footer);
            }
            finally
            {
                GUI.matrix = savedMatrix;
                GUI.color = savedColor;
                GUI.depth = savedDepth;
            }
        }

        private static GUIStyle PrepareStyle(GUIStyle style, int fontSize,
            FontStyle fontStyle, Color textColor)
        {
            if (style == null) style = new GUIStyle(GUI.skin.label);
            style.fontSize = fontSize;
            style.fontStyle = fontStyle;
            style.alignment = TextAnchor.MiddleCenter;
            style.wordWrap = true;
            style.normal.textColor = textColor;
            return style;
        }
    }
}
