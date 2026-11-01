using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using GlobalEnums;

namespace Yi_in_Coloseum
{
    // v1.6.1: Cloud Piercer is an instant wall-clipped beam; Thunder Buster
    // is a fast projectile with a delayed area blast; Shadow Hunter homes.
    // The original Nine Sols art and sound slots remain local to the mod.
    public class YiRanged : MonoBehaviour
    {
        public const int MaxSand = 5;
        public int Sand { get; private set; }
        public int Mode { get; private set; }
        public string ModeName
        {
            get { return Mode == 0 ? "CLOUD PIERCER" : Mode == 1 ? "THUNDER BUSTER" : "SHADOW HUNTER"; }
        }

        private const float Cooldown = 0.53f;
        private const float SlowFactor = 0.46f;
        private const float BowReleaseDelay = 0.45f;
        private const float BeamDuration = 0.17f;
        private const float ThunderSpeed = 300f;
        private const float HunterSpeed = 300f;
        private const float MaxRange = 16f;

        private HeroController owner;
        private YiAudio sfx;
        private Texture2D[] artwork = new Texture2D[3];
        private GUIStyle labelStyle;
        private float nextShot;
        private float facing = 1f;
        private int visualMode;
        private bool waitingRelease;
        private float releaseAt;
        private bool beamActive;
        private float beamEnds;
        private Vector3 beamStart, beamEnd;
        private bool inFlight;
        private Vector3 position, previousPosition, forward;
        private float distanceTravelled;
        private float flightEnds;
        private HealthManager homingTarget;
        private HealthManager[] nearbyEnemies = new HealthManager[0];
        private float nextEnemyScan;
        private float nextRetarget;
        private readonly Vector3[] pathHistory = new Vector3[9];
        private int historyCount;
        private int burstMode = -1;
        private int ticksLeft;
        private float nextTick;
        private Vector3 burstPoint;
        private HealthManager burstTarget;
        private bool blastVisual;
        private float blastExpires;
        private float slowEnds;
        private bool ownsSlowMotion;
        private float savedScale, savedFixedStep;
        private int terrainMask;

        private void Awake()
        {
            owner = GetComponent<HeroController>();
            sfx = GetComponent<YiAudio>();
            Sand = MaxSand;
            int layer = LayerMask.NameToLayer("Terrain");
            terrainMask = layer >= 0 ? (1 << layer) : 0;
            string assets = Path.Combine(Path.GetDirectoryName(
                Assembly.GetExecutingAssembly().Location), "Assets");
            artwork[0] = LoadArt(Path.Combine(assets, "ProjectilePiercer.png"));
            artwork[1] = LoadArt(Path.Combine(assets, "ProjectileThunder.png"));
            artwork[2] = LoadArt(Path.Combine(assets, "ProjectileHunter.png"));
            Debug.Log("YI_V162_FAST_SWEPT_BOW_READY");
        }

        private static Texture2D LoadArt(string file)
        {
            if (!File.Exists(file)) return null;
            try
            {
                Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!texture.LoadImage(File.ReadAllBytes(file)))
                {
                    UnityEngine.Object.Destroy(texture);
                    return null;
                }
                texture.filterMode = FilterMode.Bilinear;
                return texture;
            }
            catch (Exception ex) { Debug.LogWarning("Yi bow asset: " + ex.Message); return null; }
        }

        public void Refill() { Sand = MaxSand; }
        public void CycleMode()
        {
            Mode = (Mode + 1) % 3;
            if (sfx == null) sfx = GetComponent<YiAudio>();
            if (sfx != null) sfx.Play("menu_select", 0.65f);
        }
        public void Fire(HeroController hero, YiAnimationOverlay view)
        {
            if (hero == null || Time.time < nextShot || Time.timeScale < 0.01f || waitingRelease) return;
            if (Sand == 0)
            {
                if (sfx != null) sfx.Play("bow_no_ammo", 0.50f);
                return;
            }
            owner = hero;
            Sand--;
            nextShot = Time.time + Cooldown;
            facing = hero.cState.facingRight ? 1f : -1f;
            visualMode = Mode;
            waitingRelease = true;
            releaseAt = Time.unscaledTime + BowReleaseDelay;
            if (view != null) view.TriggerBow(!hero.cState.onGround);
            BeginCinematic(); // cinematic occurs BEFORE the firing frame
        }

        private void BeginCinematic()
        {
            if (ownsSlowMotion || Time.timeScale < 0.80f) return;
            savedScale = Time.timeScale;
            savedFixedStep = Time.fixedDeltaTime;
            Time.timeScale = savedScale * SlowFactor;
            Time.fixedDeltaTime = savedFixedStep * SlowFactor;
            slowEnds = Time.unscaledTime + 0.60f;
            ownsSlowMotion = true;
        }
        private void RestoreSpeed()
        {
            if (!ownsSlowMotion) return;
            if (Mathf.Approximately(Time.timeScale, savedScale * SlowFactor))
                Time.timeScale = savedScale;
            if (Mathf.Approximately(Time.fixedDeltaTime, savedFixedStep * SlowFactor))
                Time.fixedDeltaTime = savedFixedStep;
            ownsSlowMotion = false;
        }

        private float TerrainDistance(Vector3 from, Vector2 dir, float maximum)
        {
            if (terrainMask == 0) return maximum;
            RaycastHit2D result = Physics2D.Raycast(new Vector2(from.x, from.y),
                dir.normalized, maximum, terrainMask);
            return result.collider == null ? maximum : Mathf.Max(0f, result.distance - 0.08f);
        }
        private HealthManager[] CollectEnemies()
        {
            nearbyEnemies = FindObjectsOfType<HealthManager>();
            nextEnemyScan = Time.unscaledTime + 0.3f;
            return nearbyEnemies;
        }
        private HealthManager ClosestEnemy(Vector3 here, float radius)
        {
            float bestSq = radius * radius;
            HealthManager nearest = null;
            foreach (HealthManager enemy in CollectEnemies())
            {
                if (enemy == null || enemy.hp <= 0) continue;
                float d = (enemy.transform.position - here).sqrMagnitude;
                if (d < bestSq) { bestSq = d; nearest = enemy; }
            }
            return nearest; // no facing restriction: actual auto-aim
        }
        private void Damage(HealthManager enemy, int amount)
        {
            if (enemy == null || enemy.hp <= 0 || owner == null) return;
            enemy.Hit(new HitInstance
            {
                Source = owner.gameObject, AttackType = AttackTypes.Spell,
                DamageDealt = amount, Multiplier = 1f,
                Direction = facing > 0 ? 0f : 180f,
                CircleDirection = false, IgnoreInvulnerable = false
            });
            if (sfx != null) sfx.Play("bow_hit", 0.52f);
        }

        private void ReleaseShot()
        {
            if (owner == null || YiRespawnGate.IsHidden(owner)) return;
            waitingRelease = false;
            RestoreSpeed();
            Vector3 start = owner.transform.position + new Vector3(facing * 0.52f, 0.73f, 0f);
            forward = new Vector3(facing, -0.025f, 0f).normalized;
            if (sfx == null) sfx = GetComponent<YiAudio>();
            if (sfx != null) sfx.Bow(visualMode);
            beamActive = false;
            inFlight = false;
            burstMode = -1;
            ticksLeft = 0;
            historyCount = 0;
            if (visualMode == 0)
            {
                // Cloud Piercer: all damage is applied immediately along the
                // segment, which stops exactly at the first solid terrain wall.
                beamStart = start;
                float length = TerrainDistance(start, forward, MaxRange);
                beamEnd = start + forward * length;
                beamEnds = Time.unscaledTime + BeamDuration;
                beamActive = length > 0.1f;
                foreach (HealthManager enemy in CollectEnemies())
                {
                    if (enemy == null || enemy.hp <= 0) continue;
                    Vector3 delta = enemy.transform.position - beamStart;
                    float along = Vector3.Dot(delta, forward);
                    if (along < 0f || along > length) continue;
                    float dist = (delta - forward * along).magnitude;
                    if (dist <= 1.12f) Damage(enemy, 30); // unchanged v1.6 damage
                }
            }
            else
            {
                position = start;
                previousPosition = start;
                distanceTravelled = 0f;
                flightEnds = Time.unscaledTime + 2f;
                inFlight = true;
                if (visualMode == 2) homingTarget = ClosestEnemy(position, 14f);
                else homingTarget = null;
                CollectEnemies();
            }
        }

        private void DetonateThunder()
        {
            if (!inFlight) return;
            inFlight = false;
            burstMode = 1;
            ticksLeft = 5;
            nextTick = Time.unscaledTime;
            burstPoint = position;
            CollectEnemies();
            blastVisual = true;
            blastExpires = Time.unscaledTime + 0.55f;
        }
        private void ReachHunter()
        {
            if (!inFlight) return;
            inFlight = false;
            if (homingTarget != null && homingTarget.hp > 0)
            {
                burstTarget = homingTarget;
                Damage(burstTarget, 6);
                ticksLeft = 2;
                nextTick = Time.unscaledTime + 0.22f;
                burstMode = 2;
            }
        }
        private void RecordTrail()
        {
            for (int i = pathHistory.Length - 1; i > 0; --i)
                pathHistory[i] = pathHistory[i - 1];
            pathHistory[0] = position;
            historyCount = Mathf.Min(historyCount + 1, pathHistory.Length);
        }

        private static float DistanceToSegmentSquared(Vector3 point, Vector3 from, Vector3 to)
        {
            Vector3 delta = to - from;
            float lengthSq = delta.sqrMagnitude;
            if (lengthSq < 0.0001f) return (point - from).sqrMagnitude;
            float t = Mathf.Clamp01(Vector3.Dot(point - from, delta) / lengthSq);
            return (point - (from + delta * t)).sqrMagnitude;
        }

        private void Update()
        {
            // Never release or advance attacks while the game is paused.
            if (Time.timeScale < 0.01f) return;
            if (waitingRelease && Time.unscaledTime >= releaseAt) ReleaseShot();
            if (ownsSlowMotion && Time.unscaledTime >= slowEnds) RestoreSpeed();
            if (YiRespawnGate.IsHidden(owner))
            {
                waitingRelease = false; inFlight = false; beamActive = false;
                RestoreSpeed(); return;
            }
            if (beamActive && Time.unscaledTime >= beamEnds) beamActive = false;
            if (inFlight)
            {
                float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
                previousPosition = position;
                if (visualMode == 2)
                {
                    if ((homingTarget == null || homingTarget.hp <= 0) &&
                        Time.unscaledTime >= nextRetarget)
                    {
                        nextRetarget = Time.unscaledTime + 0.18f;
                        homingTarget = ClosestEnemy(position, 14f);
                    }
                    Vector3 aim = homingTarget != null ?
                        (homingTarget.transform.position - position).normalized : forward;
                    // Gradual turning rather than instantly teleporting to a target.
                    forward = Vector3.RotateTowards(forward, aim, dt * 24f, 0f);
                    float step = HunterSpeed * dt;
                    // Avoid flying past the target in one frame at 300 units/s.
                    float remaining = homingTarget != null ?
                        Vector3.Distance(position, homingTarget.transform.position) : step;
                    position += forward * Mathf.Min(step, remaining);
                }
                else position += forward * ThunderSpeed * dt;
                distanceTravelled += (position - previousPosition).magnitude;
                RecordTrail();
                float travelled = (position - previousPosition).magnitude;
                bool terrainHit = travelled > 0.001f &&
                    TerrainDistance(previousPosition, (position - previousPosition).normalized,
                        travelled + 0.08f) < travelled;
                if (terrainHit || distanceTravelled > MaxRange || Time.unscaledTime >= flightEnds)
                {
                    if (visualMode == 1) DetonateThunder(); else ReachHunter();
                }
                else if (visualMode == 2)
                {
                    if (homingTarget != null &&
                        DistanceToSegmentSquared(homingTarget.transform.position,
                            previousPosition, position) < 0.95f)
                        ReachHunter();
                }
                else
                {
                    // Fast bomb projectile: burst on first enemy encountered.
                    foreach (HealthManager enemy in nearbyEnemies)
                    {
                        if (enemy == null || enemy.hp <= 0) continue;
                        if (DistanceToSegmentSquared(enemy.transform.position,
                            previousPosition, position) < 0.95f)
                        { DetonateThunder(); break; }
                    }
                }
            }
            if (ticksLeft > 0 && Time.unscaledTime >= nextTick)
            {
                if (burstMode == 1)
                {
                    foreach (HealthManager enemy in nearbyEnemies)
                        if (enemy != null && enemy.hp > 0 &&
                            (enemy.transform.position - burstPoint).sqrMagnitude < 5f)
                            Damage(enemy, 4); // five 4-damage ticks, unchanged
                    nextTick += 0.18f;
                }
                else if (burstMode == 2)
                {
                    if (burstTarget != null && burstTarget.hp > 0) Damage(burstTarget, 6);
                    nextTick += 0.22f;
                }
                ticksLeft--;
                if (ticksLeft == 0) burstMode = -1;
            }
        }
        private void OnDisable() { waitingRelease = false; RestoreSpeed(); }
        private void OnDestroy()
        {
            RestoreSpeed();
            foreach (Texture2D t in artwork) if (t != null) Destroy(t);
        }

        private void OnGUI()
        {
            if (Time.timeScale < 0.01f || YiRespawnGate.IsHidden(owner)) return;
            Color old = GUI.color;
            Matrix4x4 oldMatrix = GUI.matrix;
            int oldDepth = GUI.depth;
            try
            {
                GUI.depth = -45;
                float bottom = Screen.height - 230f;
                GUI.color = new Color(0.025f, 0.06f, 0.11f, 0.86f);
                GUI.DrawTexture(new Rect(20f, bottom, 615f, 205f), Texture2D.whiteTexture);
                if (labelStyle == null)
                {
                    labelStyle = new GUIStyle(GUI.skin.label);
                    labelStyle.fontStyle = FontStyle.Bold;
                }
                labelStyle.fontSize = 29;
                labelStyle.normal.textColor = new Color(0.54f, 0.93f, 1f);
                GUI.color = Color.white;
                GUI.Label(new Rect(43, bottom + 19, 540, 44), ModeName, labelStyle);
                for (int i = 0; i < MaxSand; i++)
                {
                    GUI.color = i < Sand ? new Color(0.31f, 0.75f, 1f) :
                        new Color(0.16f, 0.24f, 0.32f);
                    GUI.DrawTexture(new Rect(44 + i * 91, bottom + 92, 65, 35),
                                    Texture2D.whiteTexture);
                }
                GUI.color = Color.white;
                DrawWorldEffects();
            }
            finally { GUI.color = old; GUI.matrix = oldMatrix; GUI.depth = oldDepth; }
        }
        private void DrawWorldEffects()
        {
            if (Camera.main == null) return;
            Texture2D texture = artwork[visualMode];
            if (texture == null) return;
            if (beamActive)
            {
                Vector3 a = Camera.main.WorldToScreenPoint(beamStart);
                Vector3 b = Camera.main.WorldToScreenPoint(beamEnd);
                if (a.z > 0 && b.z > 0)
                {
                    float fade = Mathf.Clamp01((beamEnds - Time.unscaledTime) / BeamDuration);
                    Vector2 p1 = new Vector2(a.x, Screen.height - a.y);
                    Vector2 p2 = new Vector2(b.x, Screen.height - b.y);
                    Vector2 center = (p1 + p2) * 0.5f;
                    float length = Vector2.Distance(p1, p2);
                    GUI.color = new Color(0.69f, 1f, 1f, fade);
                    DrawOriented(new Rect(center.x-length/2f, center.y-40f, length, 80f),
                        texture, center,
                        Mathf.Atan2((p2-p1).y, (p2-p1).x) * Mathf.Rad2Deg);
                }
                return;
            }
            if (inFlight)
            {
                // Moving sprite and cached translucent afterimages form the trail.
                Vector3 sp = Camera.main.WorldToScreenPoint(position);
                if (sp.z <= 0f) return;
                Vector2 center = new Vector2(sp.x, Screen.height - sp.y);
                float angle = Mathf.Atan2(-forward.y, forward.x) * Mathf.Rad2Deg;
                float w = visualMode == 1 ? 128f : 105f;
                float h = visualMode == 1 ? 61f : 55f;
                for (int i = historyCount - 1; i >= 1; i--)
                {
                    Vector3 t = Camera.main.WorldToScreenPoint(pathHistory[i]);
                    if (t.z <= 0f) continue;
                    Vector2 q = new Vector2(t.x, Screen.height - t.y);
                    float f = 1f - i / (float)pathHistory.Length;
                    GUI.color = new Color(0.52f, visualMode == 1 ? 0.73f : 1f,
                        visualMode == 1 ? 1f : 0.72f, 0.18f * f);
                    DrawOriented(new Rect(q.x-w*.4f, q.y-h*.4f, w*.8f, h*.8f),
                        texture, q, angle);
                }
                GUI.color = Color.white;
                DrawOriented(new Rect(center.x-w/2f, center.y-h/2f, w, h), texture, center, angle);
            }
            if (blastVisual && Time.unscaledTime < blastExpires)
            {
                Vector3 sp = Camera.main.WorldToScreenPoint(burstPoint);
                if (sp.z > 0f)
                {
                    float k = 1f - (blastExpires - Time.unscaledTime) / 0.55f;
                    float radius = 65f + k * 90f;
                    GUI.color = new Color(0.79f, 0.85f, 1f, 0.65f * (1f-k));
                    GUI.DrawTexture(new Rect(sp.x-radius, Screen.height-sp.y-radius,
                        radius*2, radius*2), texture, ScaleMode.ScaleToFit, true);
                }
            }
        }
        private static void DrawOriented(Rect rect, Texture2D texture,
            Vector2 pivot, float angle)
        {
            Matrix4x4 old = GUI.matrix;
            try
            {
                GUIUtility.RotateAroundPivot(angle, pivot);
                GUI.DrawTexture(rect, texture, ScaleMode.StretchToFill, true);
            }
            finally { GUI.matrix = old; }
        }
    }
}
