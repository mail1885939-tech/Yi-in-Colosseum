using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Globalization;
using UnityEngine;

namespace Yi_in_Coloseum
{
    // v1.2.1 repair: visual-only overlay. Gameplay stays in Class1.cs.
    public class YiAnimationOverlay : MonoBehaviour
    {
        private readonly Dictionary<string, List<Texture2D>> clips =
            new Dictionary<string, List<Texture2D>>();
        private readonly Queue<string> parryAnimations = new Queue<string>();
        private readonly Dictionary<Renderer, bool> originalRenderers =
            new Dictionary<Renderer, bool>();
        private Rigidbody2D body;
        private bool ready;
        private int previousHealth = -1;
        private GameObject pauseFigure;
        private SpriteRenderer pauseRenderer;
        private Sprite pauseSprite;
        private Texture2D pauseSpriteSource;
        private string state = "Idle";
        private string action = "";
        private float stateStarted;
        private float actionEnds;
        private float actionDuration;
        private float lastAttack = -100f;
        private int comboStep;
        private float deathEnds = -100f;
        private bool deathGhostRequested;
        private Vector3 oldPosition;
        private bool havePosition;
        private float speedSmooth;
        private float runningUntil;
        private bool movementWasActive;
        private float movementBeganAt;
        private float skullKickFxStarted = -100f;
        private float skullKickFxUntil = -100f;
        private float spriteHeight = 440f;
        private float verticalTrim = -15f;
        private float wallGap = 35f;
        private const float BaseFeet = 109f;
        private const float ComboWindow = 0.55f;
        private const float Attack1Length = 0.30f;
        private const float Attack2Length = 0.32f;
        private const float Attack3Length = 0.42f;
        private float hurtLockUntil;
        private float airSparkUntil = -1f;
        private float airSparkStarted = -1f;
        private float superSparkStarted = -1f;
        private float jumpSparkUntil = -1f;
        private float jumpSparkStarted = -1f;
        private float lastVy;
        private bool wasGrounded = true;
        private bool superChargeActive;
        private float superChargeFadeUntil = -1f;
        private bool chargedStrikeGlow;
        private float chargedStrikeGlowStarted;
        private float chargedStrikeImpactUntil = -1f;
        private float chargedStrikeImpactStarted;
        private float chargeSwordUntil = -1f;
        private float chargeSwordStarted = -1f;
        private bool showDebug;
        private string lastEvent = "LOADED";
        private string calibrationPath;
        private float bowStartedRealtime;

        // Public, read-only animation state for Class1.cs movement tuning.
        // Do not infer running from a dash or a velocity spike.
        public bool IsRunningAnimation
        {
            get { return ready && state == "Run" && Time.time >= actionEnds; }
        }

        private void Awake()
        {
            calibrationPath = Path.Combine(Path.GetDirectoryName(
                Assembly.GetExecutingAssembly().Location), "YiCalibration.ini");
            LoadCalibration();
            // Bow art is baked into the original firing frames in v1.4.5.
            // No extra free-floating UI texture is drawn at runtime.
            body = GetComponent<Rigidbody2D>();
            foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
            {
                if (IsKnightSpriteRenderer(r) || IsVanillaShadeRenderer(r))
                    originalRenderers[r] = r.enabled;
            }
            string root = Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Animations");
            string[] names = { "Idle", "WalkStart", "Walk", "Run", "Jump", "Fall", "SkullKick", "SkullKickFx", "Parry", "ParrySuccess",
                "AirParry", "Talisman", "Heal", "Hurt", "Die", "WallStick", "WallSlide",
                "Attack1", "Attack2", "Attack3", "Parry1", "Parry2", "Parry3",
                "BowGround", "BowAir", "TalismanDash", "Dash", "SuperCharge",
                "SuperRelease", "SuperSpark", "AirSpark", "AirJumpSpark",
                "TalismanCharge", "TalismanDetonate", "ChargeGround", "ChargeAir",
                "ChargeGlow", "ChargeImpact", "ChargeSword", "NailSlash" };
            foreach (string name in names)
            {
                List<Texture2D> frames = new List<Texture2D>();
                string folder = Path.Combine(root, name);
                if (Directory.Exists(folder))
                {
                    string[] filenames = Directory.GetFiles(folder, "*.png");
                    Array.Sort(filenames, StringComparer.OrdinalIgnoreCase);
                    foreach (string filename in filenames)
                    {
                        try
                        {
                            Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                            if (tex.LoadImage(File.ReadAllBytes(filename)))
                            {
                                tex.filterMode = FilterMode.Bilinear;
                                frames.Add(tex);
                            }
                            else Destroy(tex);
                        }
                        catch (Exception error)
                        {
                            Debug.LogWarning("Yi sprite load: " + filename + " - " + error.Message);
                        }
                    }
                }
                clips[name] = frames;
                Debug.Log("Yi v1.3.1 clip " + name + " = " + frames.Count);
            }
            ready = HasFrames("Idle");
            Debug.Log("YI_V140_ORIGINAL_ANIMATIONS_20261028 Ready=" + ready + " root=" + root);
            // Pause-only world-space sprite is drawn under the game's overlay UI.
            pauseFigure = new GameObject("YiPauseBackgroundSprite");
            pauseRenderer = pauseFigure.AddComponent<SpriteRenderer>();
            pauseRenderer.sortingOrder = 200;
            pauseRenderer.enabled = false;
            stateStarted = Time.time;
        }

        // Persist the alignment controls that previously reset on every launch.
        // This intentionally uses a tiny local INI file instead of an extra package.
        private void LoadCalibration()
        {
            try
            {
                if (!File.Exists(calibrationPath)) return;
                foreach (string raw in File.ReadAllLines(calibrationPath))
                {
                    string[] pair = raw.Split(new[] { '=' }, 2);
                    if (pair.Length != 2) continue;
                    float value;
                    if (!float.TryParse(pair[1].Trim(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out value)) continue;
                    if (pair[0].Trim() == "Height")
                        spriteHeight = Mathf.Clamp(value, 160f, 800f);
                    if (pair[0].Trim() == "VerticalTrim")
                        verticalTrim = Mathf.Clamp(value, -250f, 250f);
                    if (pair[0].Trim() == "WallGap")
                        wallGap = Mathf.Clamp(value, 0f, 100f);
                }
                Debug.Log("Yi: loaded sprite alignment from " + calibrationPath);
            }
            catch (Exception ex) { Debug.LogWarning("Yi alignment: " + ex.Message); }
        }

        private void SaveCalibration()
        {
            try
            {
                File.WriteAllLines(calibrationPath, new[] {
                    "# Yi in Coloseum visual calibration — press F6/F7/F9/F10/F11/F12",
                    "Height=" + spriteHeight.ToString(CultureInfo.InvariantCulture),
                    "VerticalTrim=" + verticalTrim.ToString(CultureInfo.InvariantCulture),
                    "WallGap=" + wallGap.ToString(CultureInfo.InvariantCulture)
                });
            }
            catch (Exception ex) { Debug.LogWarning("Yi save alignment: " + ex.Message); }
        }

        // Always render only Yi; never remove hero movement, collision or health systems.
        private static bool IsKnightSpriteRenderer(Renderer r)
        {
            return r != null && (r.gameObject.GetComponent("tk2dSprite") != null ||
                   r.gameObject.GetComponent<HeroController>() != null);
        }
        // Suppress only the Knight's known shadow-dash particle renderers.
        // Never disable enemy projectiles, camera effects, or every particle globally.
        private static bool IsVanillaShadeRenderer(Renderer r)
        {
            if (r == null) return false;
            string n = r.gameObject.name.ToLowerInvariant();
            return (n.Contains("shadow dash") || n.Contains("shade cloak") ||
                    n.Contains("shadowdash") || n.Contains("sharp shadow")) &&
                   !n.Contains("yi");
        }

        private bool HasFrames(string name)
        {
            List<Texture2D> frames;
            return !string.IsNullOrEmpty(name) &&
                clips.TryGetValue(name, out frames) && frames.Count != 0;
        }

        private void StartAction(string name, float length)
        {
            if (!HasFrames(name))
            {
                Debug.LogWarning("Yi v1.2.1 REPAIR missing action sprites: " + name);
                return;
            }
            if (Time.time < deathEnds && name != "Die") return;
            if (!name.StartsWith("Parry") && name != "AirParry")
                parryAnimations.Clear();
            action = name;
            actionDuration = Mathf.Max(0.1f, length);
            actionEnds = Time.time + actionDuration;
            state = name;
            stateStarted = Time.time;
        }

        // Called by Hollow Knight's actual AttackHook, not X or mouse polling.
        public void TriggerNailAttack()
        {
            if (Time.time < hurtLockUntil || Time.time < deathEnds) return;
            comboStep = Time.time - lastAttack <= ComboWindow ? comboStep % 3 + 1 : 1;
            lastAttack = Time.time;
            string clip = "Attack" + comboStep;
            float length = comboStep == 1 ? Attack1Length :
                           comboStep == 2 ? Attack2Length : Attack3Length;
            Debug.Log("Yi actual nail attack => " + clip);
            lastEvent = "ATTACK HOOK -> " + clip;
            StartAction(clip, length);
        }

        public void TriggerSkullKick()
        {
            lastEvent = "SKULL KICK / DOWNWARD AERIAL COUNTER";
            StartAction("SkullKick", 0.55f);
            skullKickFxStarted = Time.time;
            skullKickFxUntil = Time.time + 0.37f;
        }

        public void TriggerHeal()
        {
            Debug.Log("Yi Medicine Pipe visual triggered");
            lastEvent = "MEDICINE PIPE -> Heal";
            StartAction("Heal", 0.85f);
        }
        public void TriggerTalisman()
        {
            lastEvent = "TALISMAN DASH";
            StartAction(HasFrames("TalismanDash") ? "TalismanDash" : "Talisman", 0.40f);
        }
        // Bow is fired only by the ranged controller; attacks cannot accidentally
        // trigger the complete three-slash animation.
        public void TriggerBow(bool airborne)
        {
            string clip = airborne ? "BowAir" : "BowGround";
            lastEvent = "AZURE BOW -> " + clip;
            if (HasFrames(clip))
            {
                // Slow deliberate bow animation, independent of global slow-mo speed.
                StartAction(clip, airborne ? 1.06f : 1.12f);
                bowStartedRealtime = Time.unscaledTime;
            }
            else Debug.LogWarning("Yi: missing " + clip + " sprites; copy the supplied Animations folder");
        }
        public void TriggerParry(bool superParry, bool air)
        {
            if (superParry) TriggerSuperRelease();
            else if (air) TriggerAirParry();
            else StartAction("Parry1", 0.43f);
        }
        public void TriggerSuperCharge()
        {
            superChargeActive = true;
            superChargeFadeUntil = -1f;
            superSparkStarted = Time.time;
            lastEvent = "UNBOUNDED PREPARE";
            StartAction("SuperCharge", 0.75f);
        }
        public void CancelSuperCharge()
        {
            superChargeActive = false;
            superChargeFadeUntil = Time.time + 0.18f;
            if (action == "SuperCharge") { action = ""; actionEnds = -100f; }
        }
        public void TriggerSuperRelease()
        {
            superChargeActive = false;
            superChargeFadeUntil = -1f;
            lastEvent = "UNBOUNDED RELEASE";
            StartAction("SuperRelease", 0.62f);
            airSparkStarted = Time.time;
            airSparkUntil = Time.time + 0.50f;
        }
        private void TriggerAirParry()
        {
            lastEvent = "AIR PARRY + SPARK";
            StartAction("AirParry", 0.44f);
            airSparkStarted = Time.time;
            airSparkUntil = Time.time + 0.48f;
        }
        public void BeginChargedStrikeGlow()
        {
            chargedStrikeGlow = true;
            chargedStrikeGlowStarted = Time.time;
            lastEvent = "CHARGED STRIKE READY";
        }
        public void EndChargedStrikeGlow()
        {
            chargedStrikeGlow = false;
        }
        public void TriggerChargedStrike(bool airborne)
        {
            chargedStrikeGlow = false;
            chargedStrikeImpactStarted = Time.time;
            chargedStrikeImpactUntil = Time.time + 0.40f;
            chargeSwordStarted = Time.time + 0.14f;
            chargeSwordUntil = chargeSwordStarted + 0.27f;
            string clip = airborne ? "ChargeAir" : "ChargeGround";
            lastEvent = "CHARGED STRIKE => " + clip;
            StartAction(clip, airborne ? 0.60f : 0.72f);
        }

        public void TriggerTalismanCharge()
        {
            lastEvent = "TALISMAN FULL CONTROL CHARGE";
            StartAction("TalismanCharge", 0.8f);
        }
        public void ShowTalismanCharge(int count)
        {
            if (action != "TalismanCharge" || Time.time > actionEnds)
                StartAction("TalismanCharge", 0.8f);
        }
        public void TriggerTalismanDetonation()
        {
            lastEvent = "TALISMAN RELEASE";
            StartAction("TalismanDetonate", 0.32f);
        }
        // Three visually distinct parry motions from HoHoYee_Parry_ABC0-14.
        // Only the visual layer changes; Class1 owns block/Qi rules.
        public void TriggerParryGesture(int gesture, bool air)
        {
            int step = Mathf.Clamp(gesture, 1, 3);
            string clip = air ? "AirParry" : "Parry" + step;
            if (air)
            {
                airSparkStarted = Time.time;
                airSparkUntil = Time.time + 0.48f;
            }
            if (!HasFrames(clip)) clip = air ? "AirParry" : "Parry";
            lastEvent = "PARRY GESTURE " + step + "/3 -> " + clip;
            // Queue rapid inputs so a new keypress won't visually cut off
            // the previous gesture before its meaningful frames have played.
            bool parryPlaying = Time.time < actionEnds &&
                (action.StartsWith("Parry") || action == "AirParry");
            if (parryPlaying)
            {
                if (parryAnimations.Count < 2) parryAnimations.Enqueue(clip);
            }
            else
            {
                parryAnimations.Clear();
                StartAction(clip, 0.43f);
            }
        }
        public void CancelHeal()
        {
            if (action == "Heal") { action = ""; actionEnds = -100f; }
        }
        public void TriggerHurt()
        {
            parryAnimations.Clear();
            if (Time.time < deathEnds) return;
            hurtLockUntil = Time.time + 0.35f;
            StartAction("Hurt", 0.38f);
        }
        public void TriggerDeath()
        {
            parryAnimations.Clear();
            if (deathGhostRequested) return;
            deathGhostRequested = true;
            deathEnds = Time.time + 1.5f;
            YiRespawnGate.MarkDeath(transform.position);
            StartAction("Die", 1.45f);
            // The hero can disappear before the death frames finish. A detached visual
            // survives the hero's destruction and plays the sequence independently.
            if (Camera.main != null && HasFrames("Die"))
            {
                GameObject ghost = new GameObject("YiDeathAnimationV121");
                DontDestroyOnLoad(ghost);
                YiDeathAfterimage view = ghost.AddComponent<YiDeathAfterimage>();
                view.Initialize(Path.Combine(Path.GetDirectoryName(
                    Assembly.GetExecutingAssembly().Location), "Animations", "Die"),
                    Camera.main.WorldToScreenPoint(transform.position), spriteHeight,
                    BaseFeet + verticalTrim);
            }
        }

        private void Update()
        {
            if (!ready) return;
            if (YiRespawnGate.IsHidden(HeroController.instance))
            {
                HideKnight();
                return;
            }
            if (Input.GetKeyDown(KeyCode.F3)) showDebug = !showDebug;
            // Visual-only previews. They do not change HP, Chi or pipe charges.
            if (Input.GetKeyDown(KeyCode.F4)) TriggerNailAttack();
            if (Input.GetKeyDown(KeyCode.F5)) TriggerHeal();
            if (Input.GetKeyDown(KeyCode.F8)) TriggerSkullKick(); // animation diagnostic
            bool alignmentChanged = false;
            if (Input.GetKeyDown(KeyCode.F6)) { spriteHeight = Mathf.Min(800, spriteHeight + 30); alignmentChanged = true; }
            if (Input.GetKeyDown(KeyCode.F7)) { spriteHeight = Mathf.Max(160, spriteHeight - 30); alignmentChanged = true; }
            if (Input.GetKeyDown(KeyCode.F9)) { verticalTrim -= 5; alignmentChanged = true; }
            if (Input.GetKeyDown(KeyCode.F10)) { verticalTrim += 5; alignmentChanged = true; }
            if (Input.GetKeyDown(KeyCode.F11)) { wallGap = Mathf.Max(0, wallGap - 5); alignmentChanged = true; }
            if (Input.GetKeyDown(KeyCode.F12)) { wallGap = Mathf.Min(100, wallGap + 5); alignmentChanged = true; }
            if (alignmentChanged) SaveCalibration();
            HideKnight();

            if (PlayerData.instance != null)
            {
                int hp = PlayerData.instance.health;
                if (previousHealth >= 0 && hp < previousHealth)
                {
                    if (hp <= 0) TriggerDeath();
                    else TriggerHurt();
                }
                previousHealth = hp;
            }
            if (Time.time < deathEnds) return;
            if (superChargeActive)
            {
                // Loop the preparation animation while R is held instead of
                // falling back to an idle pose after its first seven frames.
                if (Time.time >= actionEnds) StartAction("SuperCharge", 0.65f);
            }
            if (Time.time >= actionEnds && parryAnimations.Count > 0)
            {
                string queuedParry = parryAnimations.Dequeue();
                if (HasFrames(queuedParry)) StartAction(queuedParry, 0.43f);
            }

            float vx = 0, vy = 0;
            if (body != null) { vx = body.velocity.x; vy = body.velocity.y; }
            else if (havePosition)
            {
                Vector3 delta = transform.position - oldPosition;
                vx = delta.x / Mathf.Max(Time.deltaTime, 0.001f);
                vy = delta.y / Mathf.Max(Time.deltaTime, 0.001f);
            }
            oldPosition = transform.position;
            havePosition = true;
            speedSmooth = Mathf.Lerp(speedSmooth, Mathf.Abs(vx),
                                     Mathf.Clamp01(Time.deltaTime * 12f));
            if (Mathf.Abs(vx) > 1f) runningUntil = Time.time + 0.13f;
            // Walking starts immediately; a sustained high-speed movement
            // transitions naturally to the shorter, real running loop.
            bool movingNow = speedSmooth > 0.8f || Time.time < runningUntil;
            if (movingNow && !movementWasActive) movementBeganAt = Time.time;
            movementWasActive = movingNow;

            HeroController hero = HeroController.instance;
            bool grounded = hero != null && hero.cState.onGround;
            bool wall = hero != null && !grounded && hero.cState.wallSliding;
            // The original air-jump visual is a short green particle sequence,
            // drawn only when Yi launches another upward movement in the air.
            if (!grounded && !wall && lastVy <= 0.4f && vy > 3.0f &&
                Time.time - jumpSparkStarted > 0.30f)
            {
                jumpSparkStarted = Time.time;
                jumpSparkUntil = Time.time + 0.48f;
            }
            lastVy = vy;
            wasGrounded = grounded;
            string next;
            if (Time.time < actionEnds && HasFrames(action)) next = action;
            else if (hero != null && hero.cState.dashing && HasFrames("Dash")) next = "Dash";
            else if (wall && HasFrames("WallSlide") && vy < -0.7f) next = "WallSlide";
            else if (wall && HasFrames("WallStick")) next = "WallStick";
            else if (!grounded && vy > 0.8f) next = "Jump";
            else if (!grounded && vy < -0.8f) next = "Fall";
            else if (movingNow)
            {
                float movingFor = Time.time - movementBeganAt;
                if (movingFor < 0.22f && HasFrames("WalkStart")) next = "WalkStart";
                else if (movingFor >= 0.58f && speedSmooth >= 3.0f && HasFrames("Run")) next = "Run";
                else next = HasFrames("Walk") ? "Walk" : "Run";
            }
            else next = "Idle";
            if (!HasFrames(next)) next = "Idle";
            if (next != state) { state = next; stateStarted = Time.time; }
        }

        private float nextRendererScan;
        private void HideKnight()
        {
            // Renderer component traversal allocates arrays; scan periodically, then
            // keep already-known Knight renderers suppressed every frame.
            if (Time.unscaledTime >= nextRendererScan)
            {
                nextRendererScan = Time.unscaledTime + 1.5f;
                foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
                {
                    if ((IsKnightSpriteRenderer(r) || IsVanillaShadeRenderer(r)) &&
                        !originalRenderers.ContainsKey(r))
                        originalRenderers[r] = r.enabled;
                }
            }
            foreach (Renderer r in originalRenderers.Keys)
                if (r != null && r.enabled) r.enabled = false;
        }
        private void LateUpdate()
        {
            if (!ready) return;
            HideKnight();
            UpdatePauseBackground();
        }

        private void UpdatePauseBackground()
        {
            if (pauseRenderer == null) return;
            bool paused = Time.timeScale < 0.01f;
            if (YiRespawnGate.IsHidden(HeroController.instance) ||
                !paused || Camera.main == null || !HasFrames(state))
            {
                pauseRenderer.enabled = false;
                return;
            }
            List<Texture2D> frames = clips[state];
            // Pause freezes the current pose. World renderer, not OnGUI,
            // ensures Unity's pause overlay stays in front of Yi.
            Texture2D tex = frames[0];
            if (pauseSprite == null || tex != pauseSpriteSource)
            {
                pauseRenderer.sprite = null;
                if (pauseSprite != null) Destroy(pauseSprite);
                float worldHeight = Mathf.Max(0.1f, spriteHeight *
                    (2f * Camera.main.orthographicSize / Mathf.Max(1, Screen.height)));
                float ppu = tex.height / worldHeight;
                pauseSprite = Sprite.Create(tex,
                    new Rect(0, 0, tex.width, tex.height),
                    new Vector2(0.5f, 1f - 490f / 512f), ppu);
                pauseSpriteSource = tex;
                pauseRenderer.sprite = pauseSprite;
            }
            bool wall = state == "WallStick" || state == "WallSlide";
            HeroController hero = HeroController.instance;
            bool facingRight = hero != null && hero.cState.facingRight;
            pauseRenderer.flipX = !facingRight; // same as corrected movement/wall orientation
            float unitsPerPixel = 2f * Camera.main.orthographicSize /
                                  Mathf.Max(1f, Screen.height);
            float wallX = wall ? (facingRight ? -wallGap : wallGap) * unitsPerPixel : 0f;
            pauseFigure.transform.position = transform.position +
                new Vector3(wallX, -(BaseFeet + verticalTrim) * unitsPerPixel, 0f);
            pauseRenderer.color = new Color(0.64f, 0.69f, 0.78f, 0.55f);
            pauseRenderer.enabled = true;
        }

        private void OnGUI()
        {
            if (!ready || YiRespawnGate.IsHidden(HeroController.instance) ||
                Camera.main == null || !HasFrames(state) ||
                Time.time < deathEnds || Time.timeScale < 0.01f) return;
            // The pause-only SpriteRenderer above displays Yi behind the menu.
            if (showDebug)
            {
                Matrix4x4 dm = GUI.matrix;
                Color dc = GUI.color;
                int dd = GUI.depth;
                GUI.depth = -200;
                GUI.matrix = Matrix4x4.identity;
                GUI.color = Color.white;
                GUI.Label(new Rect(20, Screen.height - 90, 760, 26),
                    "YI V162 | " + state + " | " +
                    clips[state].Count + " frames | " + lastEvent);
                GUI.Label(new Rect(20, Screen.height - 63, 900, 25),
                    "F4 Attack preview | F5 Heal preview | F9/F10 vertical | F11/F12 wall gap");
                GUI.matrix = dm;
                GUI.color = dc;
                GUI.depth = dd;
            }
            List<Texture2D> frames = clips[state];
            float elapsed = Mathf.Max(0f, state.StartsWith("Bow") ?
                Time.unscaledTime - bowStartedRealtime : Time.time - stateStarted);
            bool oneShot = state == "WalkStart" || state == "SkullKick" ||
                           state == "Heal" || state.StartsWith("Parry") ||
                           state == "AirParry" || state == "SuperRelease" ||
                           state == "Talisman" || state == "TalismanDash" ||
                           state == "TalismanDetonate" || state == "Hurt" ||
                           state.StartsWith("Attack") || state.StartsWith("Bow") ||
                           state.StartsWith("Charge");
            int frameIndex;
            if (oneShot)
            {
                float length = state == "WalkStart" ? 0.22f :
                               Mathf.Max(0.1f, actionDuration);
                frameIndex = Mathf.Clamp(Mathf.FloorToInt(elapsed / length * frames.Count),
                                         0, frames.Count - 1);
            }
            else
            {
                float fps = state == "Run" ? 13f : state == "Walk" ? 8f :
                            state == "Idle" ? 8f :
                            state == "SuperCharge" ? 11f : state == "Dash" ? 34f :
                            state == "TalismanCharge" ? 10f :
                            (state == "WallStick" || state == "WallSlide") ? 6f : 10f;
                frameIndex = Mathf.FloorToInt(elapsed * fps) % frames.Count;
            }
            Texture2D tex = frames[frameIndex];
            Vector3 pos = Camera.main.WorldToScreenPoint(transform.position);
            if (pos.z <= 0) return;
            float height = spriteHeight;
            float width = height * tex.width / Mathf.Max(1f, tex.height);
            float footY = Screen.height - pos.y +
                (BaseFeet + verticalTrim) * (Screen.height / 1080f);
            float x = pos.x - width * 0.5f;
            float y = footY - height * (490f / 512f);
            bool wall = state == "WallStick" || state == "WallSlide";
            HeroController hero = HeroController.instance;
            bool facingRight = hero != null && hero.cState.facingRight;
            if (wall)
            {
                // Hand-on-wall animation faces AWAY from the contacted wall.
                // Current HK facing is toward the wall; move the visible sprite
                // away so it never appears embedded in the wall.
                x += (facingRight ? -wallGap : wallGap) * (Screen.height / 1080f);
            }
            Rect rect = new Rect(x, y, width, height);
            Color oldColor = GUI.color;
            Matrix4x4 oldMatrix = GUI.matrix;
            int oldDepth = GUI.depth;
            try
            {
                GUI.depth = -30;
                GUI.color = Color.white;
                // Keep the proven wall orientation; reverse ONLY the normal
                // movement orientation, as reported in the gameplay test.
                bool mirror = !facingRight;
                if (mirror) GUIUtility.ScaleAroundPivot(new Vector2(-1f, 1f), rect.center);
                GUI.DrawTexture(rect, tex, ScaleMode.StretchToFill, true);
                // Bow is already composited into each firing sprite (no floating PNG).
                // Separate original effects: these are real transparent PNG frames
                // from Nine Sols, overlaid onto the body animation.
                if ((superChargeActive || Time.time < superChargeFadeUntil) && HasFrames("SuperSpark"))
                {
                    float fade = superChargeActive ? 1f :
                        Mathf.Clamp01((superChargeFadeUntil - Time.time) / 0.18f);
                    DrawEffectClip("SuperSpark", Time.time - superSparkStarted, 12f,
                        new Rect(x + width * 0.46f, y + height * 0.42f,
                            width * 0.29f, height * 0.30f),
                        new Color(0.42f, 1f, 0.69f, 0.82f * fade));
                }
                // Source footage: compact double-jump-like cloud clings to Yi while charging.
                if (chargedStrikeGlow && HasFrames("AirJumpSpark"))
                    DrawEffectClip("AirJumpSpark", Time.time - chargedStrikeGlowStarted, 14f,
                        new Rect(x + width * 0.33f, y + height * 0.29f,
                            width * 0.43f, height * 0.42f),
                        new Color(0.50f, 1f, 0.78f, 0.91f));
                // Long sword energy at Yi's chest level, oriented with his body.
                if (Time.time >= chargeSwordStarted && Time.time < chargeSwordUntil &&
                    HasFrames("ChargeSword"))
                {
                    DrawEffectClip("ChargeSword", Time.time - chargeSwordStarted, 13f,
                        new Rect(x + width * 0.51f, y + height * 0.39f,
                            width * 2.50f, height * 0.32f), Color.white);
                }
                if (state.StartsWith("Attack") && HasFrames("NailSlash") &&
                    Time.time - stateStarted < 0.23f)
                {
                    DrawEffectClip("NailSlash", Time.time - stateStarted, 16f,
                        new Rect(x + width * 0.54f, y + height * 0.22f,
                            width * 0.85f, height * 0.55f),
                        new Color(0.36f, 1f, 0.72f, 0.72f));
                }
                if (Time.time < chargedStrikeImpactUntil && HasFrames("ChargeImpact"))
                    DrawEffectClip("ChargeImpact", Time.time - chargedStrikeImpactStarted, 13f,
                        new Rect(x + width * 0.32f, y + height * 0.25f,
                            width * 0.62f, height * 0.60f),
                        Color.white);
                if (Time.time < airSparkUntil && HasFrames("AirSpark"))
                    DrawEffectClip("AirSpark", Time.time - airSparkStarted, 13f,
                        state == "SuperRelease" ?
                            new Rect(x + width * 0.45f, y + height * 0.40f,
                                width * 0.31f, height * 0.32f) :
                            new Rect(x + width * 0.10f, y + height * 0.17f,
                                width * 0.81f, height * 0.78f),
                        new Color(0.39f, 1f, 0.68f, 0.91f));
                if (Time.time < skullKickFxUntil && HasFrames("SkullKickFx"))
                    DrawEffectClip("SkullKickFx", Time.time - skullKickFxStarted, 16f,
                        new Rect(x + width * 0.23f, y + height * 0.44f,
                            width * 0.58f, height * 0.54f),
                        new Color(0.50f, 1f, 0.79f, 0.85f));
                if (Time.time < jumpSparkUntil && HasFrames("AirJumpSpark"))
                    DrawEffectClip("AirJumpSpark", Time.time - jumpSparkStarted, 16f,
                        new Rect(x, y + height * 0.25f, width, height * 0.68f),
                        new Color(0.31f, 1f, 0.61f, 0.80f));
                if (state == "Heal")
                {
                    // Visible pipe recovery feedback (sprite itself is a stand-in).
                    GUI.matrix = oldMatrix;
                    float pulse = 0.55f + 0.35f * Mathf.Sin(Time.realtimeSinceStartup * 14f);
                    GUI.color = new Color(0.25f, 1f, 0.68f, pulse);
                    GUI.DrawTexture(new Rect(pos.x - 8, y + height * 0.48f, 16, 16),
                                    Texture2D.whiteTexture);
                }
            }
            finally { GUI.matrix = oldMatrix; GUI.color = oldColor; GUI.depth = oldDepth; }
        }
        private void DrawEffectClip(string key, float elapsed, float fps, Rect bounds, Color tint)
        {
            List<Texture2D> layers;
            if (!clips.TryGetValue(key, out layers) || layers.Count == 0) return;
            int i = Mathf.Clamp(Mathf.FloorToInt(Mathf.Max(0f, elapsed) * fps), 0, layers.Count - 1);
            Color saved = GUI.color;
            GUI.color = tint;
            GUI.DrawTexture(bounds, layers[i], ScaleMode.StretchToFill, true);
            GUI.color = saved;
        }
        private void OnDisable()
        {
            RestoreKnight();
            if (pauseRenderer != null) pauseRenderer.enabled = false;
        }
        private void OnDestroy()
        {
            RestoreKnight();
            if (pauseSprite != null) Destroy(pauseSprite);
            if (pauseFigure != null) Destroy(pauseFigure);
            foreach (List<Texture2D> frames in clips.Values)
                foreach (Texture2D tex in frames) if (tex != null) Destroy(tex);
        }
        private void RestoreKnight()
        {
            foreach (KeyValuePair<Renderer, bool> kv in originalRenderers)
                if (kv.Key != null) kv.Key.enabled = kv.Value;
        }
    }

    // A small self-contained temporary sprite afterimage for the death transition.
    // The real Hollow Knight respawn/death logic remains untouched.
    public class YiDeathAfterimage : MonoBehaviour
    {
        private readonly List<Texture2D> frames = new List<Texture2D>();
        private float born;
        private Vector3 position;
        private float height;
        private float offset;
        public void Initialize(string path, Vector3 screenPosition,
                               float displayHeight, float screenOffset)
        {
            born = Time.realtimeSinceStartup;
            position = screenPosition;
            height = displayHeight;
            offset = screenOffset;
            if (Directory.Exists(path))
            {
                string[] files = Directory.GetFiles(path, "*.png");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (string f in files)
                {
                    try
                    {
                        Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        if (tex.LoadImage(File.ReadAllBytes(f))) frames.Add(tex);
                        else Destroy(tex);
                    }
                    catch (Exception error) { Debug.LogWarning("Yi death frame " + error.Message); }
                }
            }
        }
        private void Update()
        {
            if (Time.realtimeSinceStartup - born > 1.5f) Destroy(gameObject);
        }
        private void OnGUI()
        {
            if (frames.Count == 0 || position.z <= 0 ||
                Time.realtimeSinceStartup - born > 1.5f) return;
            float elapsed = Mathf.Max(0, Time.realtimeSinceStartup - born);
            int i = Mathf.Clamp(Mathf.FloorToInt(elapsed / 1.45f * frames.Count), 0, frames.Count - 1);
            Texture2D tex = frames[i];
            float w = height * tex.width / Mathf.Max(1f, tex.height);
            float footY = Screen.height - position.y + offset * (Screen.height / 1080f);
            Color c = GUI.color;
            int d = GUI.depth;
            GUI.depth = -30;
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(position.x - w/2f, footY - height * (490f/512f), w, height),
                            tex, ScaleMode.StretchToFill, true);
            GUI.color = c;
            GUI.depth = d;
        }
        private void OnDestroy()
        {
            foreach (Texture2D tex in frames) if (tex != null) Destroy(tex);
        }
    }
}
