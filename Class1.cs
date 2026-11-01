using System;
using System.Reflection;
using Modding;
using UnityEngine;
using GlobalEnums;
using HutongGames.PlayMaker;

namespace Yi_in_Coloseum
{
    // v1.2 prototype: battle resources, rest/gear interface, and conservative parry logic.
    // Hollow Knight still owns the true collision, masks, combat damage and enemy AI.
    public class YiInColoseum : Mod
    {
        private const int MaxChi = 5;
        private const int YiNormalDamage = 13; // Channelled Nail (third tier)
        private const int YiChargedDamage = 36; // Pure Nail 21 + 15
        // The player's normal movement is always 13 world units per second.
        // Sprite transitions (WalkStart -> Walk -> Run) are VISUAL ONLY.
        private const float YiBaseMoveSpeed = 13.0f;
        private HeroController movementSpeedHero;
        private float originalRunSpeed;
        private float originalWalkSpeed;
        private bool originalSpeedsCaptured;
        private float nextSpeedDebug;

        private const float PerfectWindow = 0.14f;
        private const float RegularWindow = 0.36f;
        private const float ParryCooldown = 0.58f;
        private const float SuperWindow = 0.36f;
        private const float SuperChargeTime = 0.30f;
        private const float SuperMaxHold = 2.10f; // 0.30s charge + 1.80s usable stance
        private const float PipeHealTime = 0.85f;
        private const int MaxPipeCharges = 8;
        private const float SuperCooldown = 1.4f;
        private const float TalismanDelay = 3.0f; // failsafe; G again detonates earlier
        private const float TalismanRange = 8.5f; // extended target detection
        private const int TalismanBaseDamage = 12;
        private const float FullControlStep = 0.38f; // Number of Qi charged per hold interval

        private HeroController lastHero;
        private YiContactShield contactShield;
        private int chi;
        private float parryStarted = -100f;
        private float nextParry;
        private bool superParry;
        private float internalDamage; // visual-only until a dedicated HP model is designed
        private HealthManager markedEnemy;
        private float detonationAt;
        private YiEffects effects;
        private YiRanged ranged;
        private YiAudio sfx;
        private int lastChargeSound = 1;
        private bool gearMenu;
        private int selectedGear = 1;
        private int pipeCharges = MaxPipeCharges;
        private const int YiMaxHp = 240;
        private const int PipeHealHp = 120;
        private bool healing;
        private float healFinishAt;
        private bool chargingSuper;
        private float superChargeStarted;
        // Holding the normal attack button arms Yi's Charged Strike.
        private const float StrikeHoldTime = 0.70f;
        private bool trackingStrikeKey;
        private bool strikeReady;
        private float strikeHeldSince;
        private float nextChargedStrike;
        private float nextSkullKick;
        private bool pendingChargeHit;
        private float pendingChargeHitAt;
        private Vector3 pendingChargeOrigin;
        private float pendingChargeFacing;
        private float chargeAirHoldUntil;
        private float internalDamageRecoveryStarts; // wait before red HUD debt fades
        // Root Node prototype: place at current position while standing in Colosseum.
        private bool nodePlaced;
        private Vector3 nodePosition;
        private Vector3 lockPosition;
        private bool lockPositionValid;
        // Traversal is driven separately from the talisman explosion timer.
        private bool dashActive;
        // Temporarily set the hero's own invulnerability state during the
        // talisman traversal to avoid vanilla hit flash/recoil. Restored safely.
        private object dashInvulState;
        private FieldInfo dashInvulField;
        private bool dashPreviousInvulnerable;
        private Vector3 dashFrom;
        private Vector3 dashTo;
        private float dashStarted;
        private const float DashDuration = 0.52f;
        private int talismanReservedQi;
        private float dashInvulnerableUntil;
        private bool talismanButtonHeld;
        private bool talismanCharging;
        private int talismanChargeCount;
        private float talismanChargeStart;
        // Airborne Full Control does not override gravity or player physics.
        // This avoids stuck-in-air states after taking damage or scene transitions.

        // Three-attempt, controlled parry chain. A successful parry resets it.
        // Responsive rhythm: three visual parry gestures, then a short recovery.
        private const float ChainMinGap = 0.22f;
        private const float ChainRecovery = 0.36f;
        private const float ChainResetAfter = 1.25f;
        private int parryChain;
        private float lastParryPress = -100f;

        // Cache the dedicated HUD object; never disable the game's pause UI.
        private GameObject vanillaHud;
        private float nextHudCheck;
        private bool hudFoundLogged;


        public YiInColoseum() : base("Yi in Coloseum") { }
        public override string GetVersion() { return "1.6.9-exit-visual-only"; }
        public override void Initialize()
        {
            ModHooks.HeroUpdateHook += OnHeroUpdate;
            // The exit guard lives on the hero, so it survives normal room transitions.
            // It does not redirect the player to another scene or change save data.
            ModHooks.TakeHealthHook += OnTakeHealth;
            // Earlier in the vanilla damage pipeline than TakeHealthHook;
            // prevents most hit reactions during an active talisman dash.
            ModHooks.AfterTakeDamageHook += OnBeforeHeroDamage;
            ModHooks.AttackHook += OnKnightAttack;
            ModHooks.HitInstanceHook += AdjustYiNailHit;
            // This intercepts ordinary healing; Medicine Pipe and Root Node
            // use direct, deliberate PlayerData assignments elsewhere.
            ModHooks.BeforeAddHealthHook += BlockVanillaHealing;
            Log("YI_V169_STANDALONE_CENTER_NOTICE_NO_COLLIDERS_BASE13");
        }

        // These are the actual controller speeds, not a visual-animation check.
        // A single run or walk step is 13 even during WalkStart/Walk/Run sprites.
        // Dash, wall-slide and knockback retain their own movement mechanics.
        private void ApplyYiSpeed(HeroController hero)
        {
            if (hero == null) return;
            if (movementSpeedHero != hero)
            {
                RestoreMovementSpeed();
                movementSpeedHero = hero;
                originalRunSpeed = hero.RUN_SPEED;
                originalWalkSpeed = hero.WALK_SPEED;
                originalSpeedsCaptured = true;
                nextSpeedDebug = 0f;
            }
            // Always active while the mod is loaded: no scene check and
            // no IsRunningAnimation dependency. Hollow Knight's controller
            // may recompute these fields, so we reapply them each HeroUpdate.
            hero.RUN_SPEED = YiBaseMoveSpeed;
            hero.WALK_SPEED = YiBaseMoveSpeed;
            if (Time.unscaledTime >= nextSpeedDebug)
            {
                Log("YI_V167_SPEED: WALK_SPEED=" + hero.WALK_SPEED.ToString("F1") +
                    " RUN_SPEED=" + hero.RUN_SPEED.ToString("F1"));
                nextSpeedDebug = Time.unscaledTime + 12f;
            }
        }

        private void RestoreMovementSpeed()
        {
            if (originalSpeedsCaptured && movementSpeedHero != null)
            {
                movementSpeedHero.RUN_SPEED = originalRunSpeed;
                movementSpeedHero.WALK_SPEED = originalWalkSpeed;
            }
            originalSpeedsCaptured = false;
            movementSpeedHero = null;
        }

        // The only health restores in this prototype should be our Medicine Pipe
        // and the Root Node. Stops vanilla Focus from recovering masks.
        // Note: vanilla Focus input/effects can still begin and Soul may drain;
        // stopping that cleanly requires a separate input/state integration.
        private int BlockVanillaHealing(int amount) { return 0; }

        // Intercept only Yi's ordinary slash HitInstances. No save-data nail
        // upgrades or global spell damage settings are modified.
        private HitInstance AdjustYiNailHit(Fsm owner, HitInstance hit)
        {
            if (hit.AttackType != AttackTypes.Nail || hit.Source == null) return hit;
            HeroController hero = HeroController.instance;
            if (hero == null) return hit;
            GameObject source = hit.Source;
            string sourceName = source.name ?? "";
            if (sourceName.IndexOf("Great Slash", StringComparison.OrdinalIgnoreCase) >= 0 ||
                sourceName.IndexOf("Dash Slash", StringComparison.OrdinalIgnoreCase) >= 0 ||
                sourceName.IndexOf("Cyclone", StringComparison.OrdinalIgnoreCase) >= 0)
                return hit; // don't rebalance Hollow Knight nail arts
            bool playerSlash = source == hero.gameObject ||
                source.transform.IsChildOf(hero.transform) ||
                (sourceName.IndexOf("slash", StringComparison.OrdinalIgnoreCase) >= 0 &&
                 (source.transform.position - hero.transform.position).sqrMagnitude < 20f);
            if (playerSlash) hit.DamageDealt = YiNormalDamage;
            return hit;
        }


        private int OnBeforeHeroDamage(int hazardType, int damage)
        {
            if (dashActive && Time.time < dashInvulnerableUntil) return 0;
            return damage;
        }

        private void BeginDashInvulnerability(HeroController hero)
        {
            if (hero == null || dashInvulField != null) return;
            try
            {
                object flags = hero.cState;
                if (flags == null) return;
                FieldInfo fld = flags.GetType().GetField("invulnerable",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fld == null || fld.FieldType != typeof(bool)) return;
                dashPreviousInvulnerable = (bool)fld.GetValue(flags);
                fld.SetValue(flags, true);
                dashInvulState = flags;
                dashInvulField = fld;
            }
            catch (Exception ex) { Log("Yi dash invulnerability unavailable: " + ex.Message); }
        }
        private void EndDashInvulnerability()
        {
            if (dashInvulField == null) return;
            try
            {
                if (dashInvulState != null)
                    dashInvulField.SetValue(dashInvulState, dashPreviousInvulnerable);
            }
            catch (Exception ex) { Log("Yi dash cleanup: " + ex.Message); }
            finally { dashInvulState = null; dashInvulField = null; }
        }

        private static object ReadMember(object instance, string member)
        {
            if (instance == null) return null;
            Type t = instance as Type ?? instance.GetType();
            BindingFlags f = BindingFlags.Public | BindingFlags.NonPublic |
                             (instance is Type ? BindingFlags.Static : BindingFlags.Instance);
            FieldInfo field = t.GetField(member, f);
            if (field != null) return field.GetValue(instance is Type ? null : instance);
            PropertyInfo prop = t.GetProperty(member, f);
            if (prop != null) return prop.GetValue(instance is Type ? null : instance, null);
            return null;
        }

        private void SuppressVanillaHud()
        {
            if (Time.unscaledTime < nextHudCheck) return;
            nextHudCheck = Time.unscaledTime + 0.4f;
            try
            {
                if (vanillaHud == null)
                {
                    // Look up the dedicated HK HUD object, not the menu canvas.
                    Type cameraType = typeof(HeroController).Assembly.GetType("GameCameras");
                    object cameras = cameraType != null ? ReadMember(cameraType, "instance") : null;
                    if (cameras == null && cameraType != null)
                        cameras = ReadMember(cameraType, "Instance");
                    if (cameras == null && cameraType != null)
                        cameras = ReadMember(cameraType, "SilentInstance");
                    object canvas = ReadMember(cameras, "hudCanvas");
                    GameObject go = canvas as GameObject;
                    Component component = canvas as Component;
                    if (go == null && component != null) go = component.gameObject;
                    vanillaHud = go;
                }
                if (vanillaHud != null)
                {
                    if (!hudFoundLogged)
                    {
                        Log("Yi: hiding vanilla HUD " + vanillaHud.name);
                        hudFoundLogged = true;
                    }
                    if (vanillaHud.activeSelf) vanillaHud.SetActive(false);
                }
                else if (!hudFoundLogged && Time.unscaledTime > 8f)
                {
                    // Only one message; don't spam logs every update.
                    Log("Yi: couldn't locate GameCameras.hudCanvas; vanilla HUD may remain visible");
                    hudFoundLogged = true;
                }
            }
            catch (Exception ex)
            {
                if (!hudFoundLogged) Log("Yi: HUD lookup failed: " + ex.Message);
                hudFoundLogged = true;
            }
        }

        // Link directly to the game's ATTACK EVENT. This honors remapped controls.
        private void OnKnightAttack(AttackDirection direction)
        {
            HeroController hero = HeroController.instance;
            if (hero == null) return;
            YiAnimationOverlay view = hero.GetComponent<YiAnimationOverlay>();
            // Real Nine Sols Skull Kick: DOWN + attack while airborne.
            // Handle this in the actual AttackHook so remapped attack keys work.
            bool downward = Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow) ||
                            Input.GetAxisRaw("Vertical") < -0.50f;
            if (!hero.cState.onGround && downward && Time.time >= nextSkullKick &&
                !healing && !dashActive)
            {
                TrySkullKick(hero, view);
                return;
            }
            if (view != null) view.TriggerNailAttack();
            YiAudio audio = hero.GetComponent<YiAudio>();
            if (audio != null) audio.PlayAttack();
        }

        private void OnHeroUpdate()
        {
            HeroController hero = HeroController.instance;
            if (hero == null) return;
            // Always 13 for Yi's walk and run, independently of the sprite state.
            ApplyYiSpeed(hero);
            if (lastHero != hero)
            {
                AbortTalismanState();
                lastHero = hero;
                effects = null;
                ranged = null;
                sfx = null;
                contactShield = null;
            }
            // Show an informational notice near outside exits; never alter doors or physics.
            if (hero.GetComponent<YiColosseumBoundary>() == null)
                hero.gameObject.AddComponent<YiColosseumBoundary>();
            SuppressVanillaHud();
            if (sfx == null)
            {
                sfx = hero.GetComponent<YiAudio>();
                if (sfx == null) sfx = hero.gameObject.AddComponent<YiAudio>();
            }

            YiAnimationOverlay animation = hero.GetComponent<YiAnimationOverlay>();
            if (animation == null) animation = hero.gameObject.AddComponent<YiAnimationOverlay>();
            if (effects == null)
            {
                effects = hero.GetComponent<YiEffects>();
                if (effects == null) effects = hero.gameObject.AddComponent<YiEffects>();
            }
            // Azure Bow is isolated from the parry/pipe controller: no frame-by-frame
            // enemy searches, and each mode shares a single five-charge resource.
            if (ranged == null)
            {
                ranged = hero.GetComponent<YiRanged>();
                if (ranged == null) ranged = hero.gameObject.AddComponent<YiRanged>();
            }
            if (contactShield == null)
            {
                contactShield = hero.GetComponent<YiContactShield>();
                if (contactShield == null) contactShield = hero.gameObject.AddComponent<YiContactShield>();
            }
            if (YiRespawnGate.IsHidden(hero))
            {
                pendingChargeHit = false;
                chargeAirHoldUntil = 0f;
                AbortTalismanState();
                trackingStrikeKey = false;
                strikeReady = false;
                YiAnimationOverlay view = hero.GetComponent<YiAnimationOverlay>();
                if (view != null) view.EndChargedStrikeGlow();
                return;
            }

            // N places a prototype Root Node marker near Yi (local scene only).
            // E interacts when standing close. This is not a save-point integration.
            if (Input.GetKeyDown(KeyCode.N) && hero.cState.onGround)
            {
                nodePlaced = true;
                nodePosition = hero.transform.position;
                Log("Prototype Root Node placed");
                if (sfx != null) sfx.Play("root_place");
            }
            if (Input.GetKeyDown(KeyCode.E) && nodePlaced &&
                Vector3.Distance(hero.transform.position, nodePosition) < 2.5f)
            {
                gearMenu = !gearMenu;
                if (sfx != null) sfx.Play("menu_open");
            }
            // Retain B for testing when the node isn't in the desired place.
            if (Input.GetKeyDown(KeyCode.B))
            {
                gearMenu = !gearMenu;
                if (sfx != null) sfx.Play("menu_open");
            }
            if (gearMenu)
            {
                if (Input.GetKeyDown(KeyCode.Alpha1)) { selectedGear = 1; if (sfx != null) sfx.Play("menu_select"); }
                if (Input.GetKeyDown(KeyCode.Alpha2)) { selectedGear = 2; if (sfx != null) sfx.Play("menu_select"); }
                if (Input.GetKeyDown(KeyCode.Alpha3)) { selectedGear = 3; if (sfx != null) sfx.Play("menu_select"); }
                if (Input.GetKeyDown(KeyCode.K) && ranged != null)
                    ranged.CycleMode();
                // Temporary stand-in for a Root Node, not a physical bench.
                if (Input.GetKeyDown(KeyCode.T))
                {
                    pipeCharges = MaxPipeCharges;
                    healing = false;
                    internalDamage = 0f;
                    if (PlayerData.instance != null)
                        PlayerData.instance.health = PlayerData.instance.maxHealth;
                    if (ranged != null) ranged.Refill();
                    Log("Prototype rest: pipe, Azure Sand and health restored");
                    if (sfx != null) sfx.Play("root_rest");
                }
            }
            else
            {
                // R: original Nine Sols Unbounded Counter — hold to prepare,
                // release after 0.5s to activate the short blocking window.
                if (Input.GetKeyDown(KeyCode.R) && hero.cState.onGround &&
                    Time.time >= nextParry && !healing && !dashActive)
                {
                    chargingSuper = true;
                    superParry = false;
                    superChargeStarted = Time.time;
                    animation.TriggerSuperCharge();
                    if (sfx != null) sfx.Play("super_charge");
                }
                if (chargingSuper && Input.GetKeyUp(KeyCode.R))
                {
                    chargingSuper = false;
                    if (Time.time - superChargeStarted >= SuperChargeTime &&
                        Time.time - superChargeStarted <= SuperMaxHold)
                    {
                        superParry = true;
                        parryStarted = Time.time;
                        nextParry = Time.time + SuperCooldown;
                        animation.TriggerSuperRelease();
                        if (sfx != null) sfx.Play("super_release", 0.85f);
                    }
                    else animation.CancelSuperCharge();
                }
                if (chargingSuper && (Time.time - superChargeStarted > SuperMaxHold ||
                    !hero.cState.onGround))
                {
                    chargingSuper = false;
                    animation.CancelSuperCharge();
                }
                // F remains parry; Skull Kick uses DOWN + the actual attack event.
                if (Input.GetKeyDown(KeyCode.F) &&
                    Time.time >= nextParry && !healing)
                {
                    // Animate three distinct ABC gestures, then repeat from the first.
                    // Keep a short anti-spam gap and a brief recovery after gesture 3.
                    if (Time.time - lastParryPress > ChainResetAfter || parryChain >= 3)
                        parryChain = 0;
                    parryChain++;
                    lastParryPress = Time.time;
                    if (chargingSuper) animation.CancelSuperCharge();
                    chargingSuper = false;
                    superParry = false;
                    parryStarted = Time.time;
                    nextParry = Time.time + (parryChain >= 3 ? ChainRecovery : ChainMinGap);
                    animation.TriggerParryGesture(parryChain, !hero.cState.onGround);
                    if (sfx != null) sfx.Play("parry_start", 0.68f);
                    Log("Yi ABC parry gesture " + parryChain + "/3");
                }
                // Charged Strike: hold the default nail key X, release after 0.70s.
                // The vanilla ordinary slash still happens on the initial press.
                if (Input.GetKeyDown(KeyCode.X) && !healing && !dashActive &&
                    (hero.cState.onGround || !(Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow))))
                {
                    trackingStrikeKey = true;
                    strikeReady = false;
                    strikeHeldSince = Time.time;
                }
                if (trackingStrikeKey && Input.GetKey(KeyCode.X) &&
                    !strikeReady && Time.time - strikeHeldSince >= StrikeHoldTime)
                {
                    strikeReady = true;
                    if (animation != null) animation.BeginChargedStrikeGlow();
                    if (sfx != null) sfx.Play("super_charge", 0.35f);
                }
                if (trackingStrikeKey && Input.GetKeyUp(KeyCode.X))
                {
                    trackingStrikeKey = false;
                    if (strikeReady && Time.time >= nextChargedStrike)
                        FireChargedStrike(hero, animation);
                    strikeReady = false;
                    if (animation != null) animation.EndChargedStrikeGlow();
                }
                if (Input.GetKeyDown(KeyCode.H)) BeginHeal(animation, hero);
                // K cycles the three researched Azure Bow arrow types.
                // J uses one Azure Sand charge; no firing while healing/parrying.
                if (Input.GetKeyDown(KeyCode.K) && ranged != null)
                    ranged.CycleMode();
                bool parryBusy = (Time.time - parryStarted >= 0f &&
                                   Time.time - parryStarted < RegularWindow) || chargingSuper;
                if (Input.GetKeyDown(KeyCode.J) && ranged != null &&
                    !healing && !parryBusy && !dashActive)
                    ranged.Fire(hero, animation);
                // Hold G through the talisman dash to charge 1-5 Qi.
                // Release G to detonate the exact count. A quick tap retains
                // the three-second automatic one-Qi fallback.
                if (Input.GetKeyDown(KeyCode.G) && !healing && !dashActive &&
                    markedEnemy == null)
                {
                    talismanButtonHeld = true;
                    BeginTalismanDash(hero, animation);
                }
                if (Input.GetKeyUp(KeyCode.G))
                {
                    talismanButtonHeld = false;
                    if (talismanCharging && markedEnemy != null && !dashActive)
                        Detonate(hero, talismanChargeCount);
                }
            }
            // Ground parries and pipe animation anchor Yi in place temporarily.
            // Air parries retain vertical motion to avoid breaking aerial combat.
            bool groundParryActive = hero.cState.onGround &&
                Time.time - parryStarted >= 0f &&
                Time.time - parryStarted <= (superParry ? SuperWindow : RegularWindow);
            bool shouldLock = !dashActive && (healing || gearMenu || groundParryActive || chargingSuper || talismanCharging);
            if (shouldLock && hero.cState.onGround)
            {
                if (!lockPositionValid)
                {
                    lockPosition = hero.transform.position;
                    lockPositionValid = true;
                }
                hero.transform.position = new Vector3(lockPosition.x, hero.transform.position.y, hero.transform.position.z);
                Rigidbody2D rb = hero.GetComponent<Rigidbody2D>();
                if (rb != null) rb.velocity = new Vector2(0f, rb.velocity.y);
            }
            else lockPositionValid = false;
            // HeroController uses 13 for both Walk and Run; animations still
            // transition normally. No direct velocity multiplier here.
            // Bounded 0.2s air suspension; never change gravityScale or Rigidbody constraints.
            // Any hit, landing or hero replacement cancels it immediately.
            if (chargeAirHoldUntil > 0f)
            {
                if (Time.time >= chargeAirHoldUntil || hero.cState.onGround || dashActive)
                    chargeAirHoldUntil = 0f;
                else
                {
                    Rigidbody2D holdBody = hero.GetComponent<Rigidbody2D>();
                    if (holdBody != null) holdBody.velocity = new Vector2(holdBody.velocity.x, 0f);
                }
            }
            if (pendingChargeHit && Time.time >= pendingChargeHitAt)
            {
                pendingChargeHit = false;
                ApplyChargedStrikeDamage(hero, pendingChargeOrigin, pendingChargeFacing);
            }
            if (dashActive) UpdateTalismanDash(hero);
            if (talismanCharging && markedEnemy != null && !dashActive)
            {
                int available = Mathf.Min(MaxChi, talismanReservedQi + chi);
                talismanChargeCount = Mathf.Clamp(1 +
                    Mathf.FloorToInt((Time.time - talismanChargeStart) / FullControlStep),
                    1, Mathf.Max(1, available));
                // Charging while airborne retains normal Hollow Knight gravity.
                if (animation != null) animation.ShowTalismanCharge(talismanChargeCount);
                if (lastChargeSound != talismanChargeCount)
                {
                    lastChargeSound = talismanChargeCount;
                    if (sfx != null) sfx.Play("talisman_charge", 0.58f);
                }
                // Avoid leaving the player stuck if focus/input is lost.
                if ((!talismanButtonHeld || Time.time - talismanChargeStart > 3.5f)
                    && markedEnemy != null)
                    Detonate(hero, talismanChargeCount);
            }
            if (markedEnemy != null && !dashActive && !talismanCharging &&
                Time.time >= detonationAt) Detonate(hero, 1);
            if (healing && Time.time >= healFinishAt) CompleteHeal();
            // Visual-only internal damage stays red for 2.5s, then slowly fades.
            // No actual mask loss/recovery is simulated by this display.
            if (internalDamage > 0f && Time.time >= internalDamageRecoveryStarts)
                internalDamage = Mathf.MoveTowards(internalDamage, 0f,
                    0.045f * Time.deltaTime);
            if (effects != null)
            {
                PlayerData pd = PlayerData.instance;
                effects.Chi = chi;
                effects.MaxChi = MaxChi;
                effects.PipeCharges = pipeCharges;
                effects.MaxPipeCharges = MaxPipeCharges;
                effects.IsHealing = healing;
                
                effects.MaxHp = YiMaxHp;
                effects.CurrentHp = pd != null ? Mathf.RoundToInt(YiMaxHp * ((float)pd.health / Mathf.Max(1, pd.maxHealth))) : 0;
                effects.InternalDamage = internalDamage;
                effects.GearMenu = gearMenu;
                effects.NodePlaced = nodePlaced;
                effects.NodePosition = nodePosition;
                effects.SelectedGear = selectedGear;
                effects.AzureSand = ranged != null ? ranged.Sand : 0;
                effects.BowName = ranged != null ? ranged.ModeName : "CLOUD PIERCER";
                effects.Marked = markedEnemy != null;
                effects.MarkedEnemy = markedEnemy;
                effects.TalismanCountdown = markedEnemy != null ? Mathf.Max(0f, detonationAt - Time.time) : 0f;
                effects.IsTalismanCharging = talismanCharging && markedEnemy != null;
                effects.TalismanChargeCount = talismanChargeCount;
                float elapsed = Time.time - parryStarted;
                float shownPrecise = parryChain == 1 ? 0.133f :
                    parryChain == 2 ? 0.10f : 0f;
                effects.ParryState = elapsed >= 0f && elapsed <=
                    (superParry ? SuperWindow : RegularWindow) ?
                    (superParry ? 3 : shownPrecise > 0f && elapsed <= shownPrecise ? 2 : 1) : 0;
            }
        }

        private int OnTakeHealth(int damage)
        {
            if (damage <= 0) return damage;
            if (dashActive && Time.time < dashInvulnerableUntil) return 0;
            // Never leave a character motion-suspended after a hit.
            chargeAirHoldUntil = 0f;
            pendingChargeHit = false;
            // An incoming hit before the pipe animation finishes cancels healing.
            if (healing && sfx != null) sfx.Play("heal_cancel");
            healing = false;
            // All interruption paths must leave movement under Hollow Knight's control.
            AbortTalismanState();
            trackingStrikeKey = false;
            strikeReady = false;
            HeroController currentHero = HeroController.instance;
            if (currentHero != null)
            {
                YiAnimationOverlay currentView = currentHero.GetComponent<YiAnimationOverlay>();
                if (currentView != null)
                {
                    currentView.CancelHeal();
                    currentView.CancelSuperCharge();
                }
            }
            chargingSuper = false;
            float dt = Time.time - parryStarted;
            float window = superParry ? SuperWindow : RegularWindow + (selectedGear == 2 ? 0.08f : 0f);
            if (dt < 0f || dt > window)
            {
                PlayerData pd = PlayerData.instance;
                if (pd != null && damage >= pd.health)
                {
                    HeroController hero = HeroController.instance;
                    YiRespawnGate.MarkDeath(hero != null ? hero.transform.position : Vector3.zero);
                    if (hero != null)
                    {
                        YiAnimationOverlay view = hero.GetComponent<YiAnimationOverlay>();
                        if (view != null) view.TriggerDeath();
                        if (sfx != null) sfx.Play("death");
                    }
                }
                if (sfx != null && (PlayerData.instance == null || damage < PlayerData.instance.health))
                    sfx.Play("hurt", 0.83f);
                return damage;
            }
            parryStarted = -100f;
            // Nine Sols reduces precision when repeatedly parrying empty air.
            float effectivePreciseWindow = parryChain == 1 ? 0.133f :
                parryChain == 2 ? 0.10f : 0f;
            bool perfect = superParry ||
                (effectivePreciseWindow > 0f && dt <= effectivePreciseWindow);
            if (sfx != null)
            {
                // Release already plays the Unbounded sound. A successful counter
                // receives its own contact sound, without replaying release twice.
                sfx.Play(perfect ? "parry_perfect" : "parry_imperfect", 0.95f);
                sfx.SuppressVanillaHitFor(0.24f);
            }
            if (effects != null) effects.ShowParryImpact(superParry, perfect);
            parryChain = 0; // contact ends the visual gesture chain
            // Both precise and imprecise parries generate one Qi.
            // Maxed-out Yi: Unbounded Drain always grants 2 Qi on a landed counter.
            chi = Mathf.Min(MaxChi, chi + (superParry ? 2 : 1));
            if (superParry && currentHero != null)
            {
                HealthManager nearby = FindSuperCounterTarget(currentHero);
                if (nearby != null)
                {
                    YiTemporaryStun stun = nearby.GetComponent<YiTemporaryStun>();
                    if (stun == null) stun = nearby.gameObject.AddComponent<YiTemporaryStun>();
                    stun.FreezeFor(3f);
                    Log("Unbounded Counter: nearby enemy interrupted for up to 3 seconds");
                }
            }
            // This is an explicit visual debt; we are NOT altering HP here.
            if (!perfect)
            {
                int maxHealth = PlayerData.instance != null ? Mathf.Max(1, PlayerData.instance.maxHealth) : 5;
                internalDamage = Mathf.Min(1f, internalDamage + (0.5f * damage / maxHealth));
                internalDamageRecoveryStarts = Time.time + 2.5f;
            }
            // A precise parry does not instantly erase the existing red visual debt.
            // No floating text: a tight green/cyan parry pulse and Qi slots provide feedback.
            Log((superParry ? "SUPER" : perfect ? "PERFECT" : "NORMAL") + " parry, Chi=" + chi);
            return 0; // avoids masks lost; hit reaction may still happen in Hollow Knight
        }

        // Fictional airborne kick/pogo in Hollow Knight. Uses Yi's original
        // JumpKick visuals; bounce and 1 Qi only on a valid enemy contact.
        private void TrySkullKick(HeroController hero, YiAnimationOverlay animation)
        {
            nextSkullKick = Time.time + 0.50f;
            if (animation != null) animation.TriggerSkullKick();
            // The downward kick is an animation-first action; the animation plays
            // even when there is no enemy beneath Yi.
            Vector3 start = hero.transform.position;
            HealthManager best = null;
            float closest = 5f;
            foreach (HealthManager enemy in UnityEngine.Object.FindObjectsOfType<HealthManager>())
            {
                if (enemy == null || enemy.hp <= 0) continue;
                Vector3 d = enemy.transform.position - start;
                if (d.y >= -0.05f || d.y < -2.3f || Mathf.Abs(d.x) > 1.4f) continue;
                float sq = d.sqrMagnitude;
                if (sq < closest) { closest = sq; best = enemy; }
            }
            if (best == null) return;
            best.Hit(new HitInstance
            {
                Source = hero.gameObject,
                AttackType = AttackTypes.Spell,
                DamageDealt = 7,
                Multiplier = 1f,
                Direction = 270f,
                CircleDirection = false,
                IgnoreInvulnerable = false
            });
            Rigidbody2D body = hero.GetComponent<Rigidbody2D>();
            if (body != null)
                body.velocity = new Vector2(body.velocity.x, Mathf.Max(body.velocity.y, 8.5f));
            chi = Mathf.Min(MaxChi, chi + 1);
            if (sfx != null) sfx.Play("parry_perfect", 0.55f);
            Log("Yi Skull Kick contact -> aerial bounce and +1 Qi");
        }

        private void FireChargedStrike(HeroController hero, YiAnimationOverlay animation)
        {
            if (hero == null || healing || dashActive) return;
            nextChargedStrike = Time.time + 0.8f;
            bool airborne = !hero.cState.onGround;
            if (animation != null) animation.TriggerChargedStrike(airborne);
            pendingChargeFacing = hero.cState.facingRight ? 1f : -1f;
            pendingChargeOrigin = hero.transform.position;
            // Release pose of the source animation precedes the actual strike.
            pendingChargeHit = true;
            pendingChargeHitAt = Time.time + 0.14f;
            if (airborne)
            {
                chargeAirHoldUntil = Time.time + 0.20f;
                Rigidbody2D rb = hero.GetComponent<Rigidbody2D>();
                if (rb != null) rb.velocity = new Vector2(rb.velocity.x, 0f);
            }
            Log("Yi Charged Strike released / pending chest-height hit");
        }

        private void ApplyChargedStrikeDamage(HeroController hero, Vector3 origin, float facing)
        {
            if (hero == null || YiRespawnGate.IsHidden(hero)) return;
            if (sfx != null) sfx.Play("attack_3", 0.85f);
            // Long-range horizontal strike at Yi's chest, clipped by the first wall.
            Vector2 start = new Vector2(origin.x + facing * 0.45f, origin.y + 0.70f);
            float reach = 9.0f;
            int terrain = LayerMask.NameToLayer("Terrain");
            if (terrain >= 0)
            {
                RaycastHit2D wall = Physics2D.Raycast(start, new Vector2(facing, 0f),
                    reach, 1 << terrain);
                if (wall.collider != null) reach = Mathf.Max(0f, wall.distance - 0.12f);
            }
            foreach (HealthManager enemy in UnityEngine.Object.FindObjectsOfType<HealthManager>())
            {
                if (enemy == null || enemy.hp <= 0) continue;
                Vector3 delta = enemy.transform.position - (Vector3)start;
                if (delta.x * facing < 0f || delta.x * facing > reach ||
                    Mathf.Abs(delta.y) > 1.35f) continue;
                enemy.Hit(new HitInstance
                {
                    Source = hero.gameObject,
                    AttackType = AttackTypes.Spell,
                    DamageDealt = YiChargedDamage,
                    Multiplier = 1f,
                    Direction = facing > 0f ? 0f : 180f,
                    CircleDirection = false,
                    IgnoreInvulnerable = false
                });
            }
            Log("Yi Charged Strike: chest-height long strike; reach=" + reach);
        }

        private HealthManager FindSuperCounterTarget(HeroController hero)
        {
            // The TakeHealth hook does not identify the attacking enemy.
            // Choose a close living enemy as an approximate counter target.
            if (hero == null) return null;
            HealthManager chosen = null;
            float bestSq = 3.25f * 3.25f;
            foreach (HealthManager enemy in UnityEngine.Object.FindObjectsOfType<HealthManager>())
            {
                if (enemy == null || enemy.hp <= 0) continue;
                Vector3 d = enemy.transform.position - hero.transform.position;
                if (Mathf.Abs(d.y) > 2.25f || d.sqrMagnitude >= bestSq) continue;
                bestSq = d.sqrMagnitude;
                chosen = enemy;
            }
            return chosen;
        }

        private void AbortTalismanState()
        {
            EndDashInvulnerability();
            dashActive = false;
            dashInvulnerableUntil = 0f;
            talismanCharging = false;
            talismanButtonHeld = false;
            talismanChargeCount = 0;
            talismanReservedQi = 0;
            markedEnemy = null;
            lockPositionValid = false;
        }

        private void BeginHeal(YiAnimationOverlay animation, HeroController hero)
        {
            PlayerData pd = PlayerData.instance;
            if (healing || pipeCharges <= 0 || pd == null || !hero.cState.onGround) return;
            if (pd.health >= pd.maxHealth) return;
            healing = true;
            lockPositionValid = false;
            healFinishAt = Time.time + PipeHealTime;
            if (animation != null) animation.TriggerHeal();
            if (sfx != null) sfx.Play("heal_begin");
            Log("Medicine Pipe started; charges=" + pipeCharges);
        }

        private void CompleteHeal()
        {
            healing = false;
            PlayerData pd = PlayerData.instance;
            if (pd == null || pipeCharges <= 0) return;
            // Nine Sols heals 60 of starting 120 HP. Hollow Knight uses discrete masks,
            // so half the maximum masks is only an approximate conversion.
            int healMasks = Mathf.Max(1, Mathf.CeilToInt(pd.maxHealth * (PipeHealHp / (float)YiMaxHp)));
            pd.health = Mathf.Min(pd.maxHealth, pd.health + healMasks);
            pipeCharges--;
            if (sfx != null) sfx.Play("heal_finish");
            if (effects != null && HeroController.instance != null)
                effects.Flash(HeroController.instance.transform.position, Color.green, "PIPE HEAL", 0.55f);
            Log("Medicine Pipe completed, charges left=" + pipeCharges);
        }

        private void BeginTalismanDash(HeroController hero, YiAnimationOverlay animation)
        {
            if (chi < 1 || hero == null) return;
            // Qi is reserved on dash. Two additional Qi are consumed on detonation
            // (if available), up to three total. Whiffs cost one Qi.
            chi--;
            talismanReservedQi = 1;
            talismanChargeCount = 1;
            lastChargeSound = 1;
            talismanCharging = false;
            chargingSuper = false;
            parryStarted = -100f;
            float facing = hero.cState.facingRight ? 1f : -1f;
            HealthManager nearest = null;
            float best = TalismanRange * TalismanRange;
            HealthManager[] enemies = UnityEngine.Object.FindObjectsOfType<HealthManager>();
            for (int i = 0; i < enemies.Length; i++)
            {
                HealthManager hm = enemies[i];
                if (hm == null || hm.hp <= 0) continue;
                Vector3 delta = hm.transform.position - hero.transform.position;
                if (delta.x * facing < 0.15f || Mathf.Abs(delta.y) > 1.6f) continue;
                if (delta.sqrMagnitude >= best) continue;
                best = delta.sqrMagnitude;
                nearest = hm;
            }
            dashFrom = hero.transform.position;
            float proposedTravel = nearest == null ? 5.0f :
                Mathf.Min(9.0f, Mathf.Abs(nearest.transform.position.x - dashFrom.x) + 3.1f);
            // Don't jump through walls. A waist-height cast checks static solid
            // terrain, while deliberately ignoring character colliders.
            float clearTravel = TerrainLimitedDash(dashFrom, facing, proposedTravel, nearest, hero);
            dashTo = dashFrom + new Vector3(facing * clearTravel, 0f, 0f);
            if (contactShield == null) contactShield = hero.GetComponent<YiContactShield>();
            if (contactShield == null) contactShield = hero.gameObject.AddComponent<YiContactShield>();
            // Ignore enemy body/contact colliders THROUGHOUT the dash corridor,
            // not only for the enemy that eventually receives the sigil.
            contactShield.ProtectInDashCorridor(dashFrom, dashTo, 1.9f, DashDuration + 0.30f);
            bool passedEnemy = nearest != null &&
                (nearest.transform.position.x - dashTo.x) * facing < -0.35f;
            markedEnemy = passedEnemy ? nearest : null;
            if (markedEnemy != null)
            {
                if (contactShield == null)
                    contactShield = hero.gameObject.GetComponent<YiContactShield>();
                if (contactShield == null)
                    contactShield = hero.gameObject.AddComponent<YiContactShield>();
                contactShield.ProtectFrom(markedEnemy, 4f);
                detonationAt = Time.time + TalismanDelay;
                // Persistent original Nine Sols talisman sigil is drawn on the target.
                // No text flashes over the enemy.
            }
            dashActive = true;
            dashStarted = Time.time;
            dashInvulnerableUntil = Time.time + DashDuration;
            BeginDashInvulnerability(hero);
            if (animation != null) animation.TriggerTalisman();
            if (sfx != null) sfx.Play("talisman_dash");
            Log("Yi talisman dash: " + (passedEnemy ? "attached" : "whiff/blocked"));
        }

        private float TerrainLimitedDash(Vector3 from, float facing, float wanted,
                                         HealthManager target, HeroController hero)
        {
            float allowed = wanted;
            Vector2 origin = new Vector2(from.x, from.y + 0.65f);
            RaycastHit2D[] hits = Physics2D.RaycastAll(origin, new Vector2(facing, 0f), wanted);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider2D collider = hits[i].collider;
                if (collider == null || collider.isTrigger) continue;
                if (collider.transform.IsChildOf(hero.transform)) continue;
                if (target != null && collider.transform.IsChildOf(target.transform)) continue;
                // Enemies can be passed through. Static scenery cannot.
                if (collider.GetComponentInParent<HealthManager>() != null) continue;
                if (collider.attachedRigidbody != null &&
                    collider.attachedRigidbody.bodyType == RigidbodyType2D.Dynamic) continue;
                allowed = Mathf.Min(allowed, Mathf.Max(0f, hits[i].distance - 0.40f));
            }
            return Mathf.Max(0f, allowed);
        }

        private void UpdateTalismanDash(HeroController hero)
        {
            float progress = Mathf.Clamp01((Time.time - dashStarted) / DashDuration);
            // Smoothly move the actual hitbox through the enemy, not just the art.
            float eased = progress * progress * (3f - 2f * progress);
            Vector3 position = Vector3.Lerp(dashFrom, dashTo, eased);
            position.y = hero.transform.position.y; // retain ground/air physics
            hero.transform.position = position;
            Rigidbody2D rb = hero.GetComponent<Rigidbody2D>();
            if (rb != null) rb.velocity = new Vector2(0f, rb.velocity.y);
            if (progress >= 1f)
            {
                dashActive = false;
                EndDashInvulnerability();
                lockPositionValid = false;
                if (markedEnemy != null && markedEnemy.hp > 0)
                {
                    YiTemporaryStun stun = markedEnemy.gameObject.GetComponent<YiTemporaryStun>();
                    if (stun == null)
                        stun = markedEnemy.gameObject.AddComponent<YiTemporaryStun>();
                    stun.FreezeFor(3.50f);
                    if (sfx != null) sfx.Play("talisman_mark");
                }
                // Charge only when the dash actually attached a talisman
                // AND G remains held. Quick taps auto-detonate after 3s.
                if (markedEnemy != null && talismanButtonHeld)
                {
                    talismanCharging = true;
                    talismanChargeStart = Time.time;
                    talismanChargeCount = 1;
                    // During charge the Knight keeps its own jump/fall physics.
                    YiAnimationOverlay overlay = hero.GetComponent<YiAnimationOverlay>();
                    if (overlay != null) overlay.TriggerTalismanCharge();
                }
            }
        }

        private void Detonate(HeroController hero, int requestedQi)
        {
            HealthManager target = markedEnemy;
            markedEnemy = null;
            talismanCharging = false;
            talismanButtonHeld = false;
            if (target == null || target.hp <= 0 || hero == null)
            {
                talismanReservedQi = 0;
                return;
            }
            int additionalQi = Mathf.Min(Mathf.Max(0, requestedQi - 1), chi);
            chi -= additionalQi;
            int totalQi = Mathf.Max(1, talismanReservedQi + additionalQi);
            talismanReservedQi = 0;
            talismanChargeCount = 0;
            YiAnimationOverlay overlay = hero.GetComponent<YiAnimationOverlay>();
            if (overlay != null) overlay.TriggerTalismanDetonation();
            int amount = TalismanBaseDamage + totalQi * 13 +
                (selectedGear == 3 ? 4 : selectedGear == 2 ? -2 : 0);
            Vector3 location = target.transform.position;
            HitInstance hit = new HitInstance
            {
                Source = hero.gameObject, AttackType = AttackTypes.Spell,
                DamageDealt = amount, Multiplier = 1f,
                Direction = hero.cState.facingRight ? 0f : 180f,
                CircleDirection = false, IgnoreInvulnerable = false
            };
            target.Hit(hit);
            if (sfx != null) sfx.Play("talisman_burst");
            // Sprite detonation animation provides feedback, without floating labels.
            Log("Yi talisman detonated: " + totalQi + " Qi, " + amount + " damage");
        }
    }

    // Targeted temporary stun: freezes the target's Rigidbody and its PlayMaker
    // state machines where available. Original states are restored on EVERY exit.
    public class YiTemporaryStun : MonoBehaviour
    {
        private Rigidbody2D body;
        private RigidbodyConstraints2D originalConstraints;
        private bool physicsFrozen;
        private readonly System.Collections.Generic.List<Behaviour> disabledAI =
            new System.Collections.Generic.List<Behaviour>();
        private float until;

        public void FreezeFor(float seconds)
        {
            until = Mathf.Max(until, Time.time + Mathf.Max(0f, seconds));
            if (body == null) body = GetComponentInChildren<Rigidbody2D>();
            if (body != null && body.bodyType == RigidbodyType2D.Dynamic && !physicsFrozen)
            {
                originalConstraints = body.constraints;
                physicsFrozen = true;
                body.velocity = Vector2.zero;
                body.constraints = RigidbodyConstraints2D.FreezeAll;
            }
            // Some Hollow Knight enemies move directly using PlayMaker FSMs.
            // Only pause their movement controllers, not HealthManager or renderers.
            foreach (Behaviour controller in GetComponentsInChildren<Behaviour>(true))
            {
                if (controller == null || !controller.enabled ||
                    controller.GetType().Name != "PlayMakerFSM") continue;
                disabledAI.Add(controller);
                controller.enabled = false;
            }
        }
        private void Update()
        {
            if (Time.time < until) return;
            Restore();
            Destroy(this);
        }
        private void OnDisable() { Restore(); }
        private void OnDestroy() { Restore(); }
        private void Restore()
        {
            if (physicsFrozen && body != null) body.constraints = originalConstraints;
            physicsFrozen = false;
            foreach (Behaviour controller in disabledAI)
                if (controller != null) controller.enabled = true;
            disabledAI.Clear();
        }
    }

    // Scoped contact-collider protection for a marked enemy (four seconds).
    // Does not suppress all hero damage, environmental hazards or other enemies.
    public class YiContactShield : MonoBehaviour
    {
        private struct Pair
        {
            public Collider2D Hero;
            public Collider2D Enemy;
            public float Until;
        }
        private readonly System.Collections.Generic.List<Pair> pairs =
            new System.Collections.Generic.List<Pair>();

        public void ProtectInDashCorridor(Vector3 start, Vector3 end,
            float halfHeight, float duration)
        {
            float minX = Mathf.Min(start.x, end.x) - 1.0f;
            float maxX = Mathf.Max(start.x, end.x) + 1.0f;
            foreach (HealthManager enemy in UnityEngine.Object.FindObjectsOfType<HealthManager>())
            {
                if (enemy == null || enemy.hp <= 0) continue;
                Vector3 pos = enemy.transform.position;
                if (pos.x < minX || pos.x > maxX ||
                    Mathf.Abs(pos.y - start.y) > halfHeight) continue;
                ProtectFrom(enemy, duration);
            }
        }

        public void ProtectFrom(HealthManager enemy, float duration)
        {
            if (enemy == null) return;
            Collider2D[] heroes = GetComponentsInChildren<Collider2D>(true);
            Collider2D[] targets = enemy.GetComponentsInChildren<Collider2D>(true);
            for (int i = 0; i < heroes.Length; i++)
                for (int j = 0; j < targets.Length; j++)
                {
                    Collider2D h = heroes[i], e = targets[j];
                    // Hero's solid collider against both enemy body and contact triggers.
                    if (h == null || e == null || h.isTrigger) continue;
                    int existing = -1;
                    for (int k = 0; k < pairs.Count; k++)
                        if (pairs[k].Hero == h && pairs[k].Enemy == e) { existing = k; break; }
                    if (existing >= 0)
                    {
                        Pair p = pairs[existing]; p.Until = Mathf.Max(p.Until, Time.time + duration);
                        pairs[existing] = p;
                    }
                    else if (!Physics2D.GetIgnoreCollision(h, e))
                    {
                        Physics2D.IgnoreCollision(h, e, true);
                        pairs.Add(new Pair { Hero = h, Enemy = e, Until = Time.time + duration });
                    }
                }
        }
        private void Update()
        {
            for (int i = pairs.Count - 1; i >= 0; i--)
            {
                Pair p = pairs[i];
                if (p.Hero != null && p.Enemy != null && Time.time < p.Until) continue;
                if (p.Hero != null && p.Enemy != null)
                    Physics2D.IgnoreCollision(p.Hero, p.Enemy, false);
                pairs.RemoveAt(i);
            }
        }
        private void OnDisable() { Restore(); }
        private void OnDestroy() { Restore(); }
        private void Restore()
        {
            foreach (Pair p in pairs)
                if (p.Hero != null && p.Enemy != null)
                    Physics2D.IgnoreCollision(p.Hero, p.Enemy, false);
            pairs.Clear();
        }
    }

    // Static gate survives HeroController replacement during death/scene loading.
    public static class YiRespawnGate
    {
        private static bool pending;
        private static float began;
        private static Vector3 deathPosition;
        public static void MarkDeath(Vector3 where)
        {
            if (pending) return;
            pending = true;
            began = Time.unscaledTime;
            deathPosition = where;
        }
        public static bool IsHidden(HeroController hero)
        {
            if (!pending) return false;
            float elapsed = Time.unscaledTime - began;
            if (elapsed < 3.75f) return true;
            PlayerData pd = PlayerData.instance;
            if (hero == null || pd == null || pd.health <= 0) return true;
            // The Knight must have actually respawned; fail-open after a long
            // healthy interval to avoid permanent invisibility on same-point reloads.
            if (Vector3.Distance(hero.transform.position, deathPosition) > 2.25f || elapsed > 8f)
            {
                pending = false;
                return false;
            }
            return true;
        }
    }

    public class YiEffects : MonoBehaviour
    {
        public int Chi, MaxChi = 5, PipeCharges = 8, MaxPipeCharges = 8, CurrentHp, MaxHp = 240, ParryState, SelectedGear = 1;
        public bool IsHealing;
        public int AzureSand;
        public string BowName = "CLOUD PIERCER";
        public float InternalDamage, TalismanCountdown;
        public bool GearMenu, Marked, NodePlaced;
        public HealthManager MarkedEnemy;
        public Vector3 NodePosition;
        private Vector3 effectAt;
        private Color effectColor = Color.white;
        private string effectMessage = "";
        private float effectUntil;
        private GUIStyle labelStyle;
        private Texture2D vialFrame;
        private Texture2D qiFilled, qiEmpty, talismanMark;
        private string assetFolder;
        // Short visual hitstop-style flash; no text overlays.
        private float parryPulseUntil;
        private float parryPulseStart;
        private Color parryPulseColor = Color.cyan;
        public void ShowParryImpact(bool unbounded, bool precise)
        {
            parryPulseStart = Time.unscaledTime;
            parryPulseUntil = Time.unscaledTime + 0.31f;
            parryPulseColor = unbounded ? new Color(0.42f, 1f, 0.66f) :
                precise ? new Color(0.30f, 0.98f, 0.92f) : new Color(1f, 0.72f, 0.30f);
        }
        public bool IsTalismanCharging;
        public int TalismanChargeCount;
        private void Awake()
        {
            try
            {
                string folder = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                    "Assets");
                assetFolder = folder;
                qiFilled = ReadArt("QiFilled.png");
                qiEmpty = ReadArt("QiEmpty.png");
                talismanMark = ReadArt("TalismanMark.png");
                string path = System.IO.Path.Combine(folder, "PipeVialFrame.png");
                if (System.IO.File.Exists(path))
                {
                    Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (texture.LoadImage(System.IO.File.ReadAllBytes(path)))
                    {
                        texture.filterMode = FilterMode.Bilinear;
                        vialFrame = texture;
                    }
                    else Destroy(texture);
                }
                else Debug.LogWarning("Yi: missing original PipeVialFrame.png at " + path);
            }
            catch (Exception ex) { Debug.LogWarning("Yi pipe UI: " + ex.Message); }
        }
        private Texture2D ReadArt(string filename)
        {
            try
            {
                string path = System.IO.Path.Combine(assetFolder, filename);
                if (!System.IO.File.Exists(path))
                {
                    Debug.LogWarning("Yi cosmetic asset missing: " + path);
                    return null;
                }
                Texture2D art = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (art.LoadImage(System.IO.File.ReadAllBytes(path)))
                {
                    art.filterMode = FilterMode.Bilinear;
                    return art;
                }
                Destroy(art);
            }
            catch (Exception ex) { Debug.LogWarning("Yi cosmetic art: " + ex.Message); }
            return null;
        }
        private void OnDestroy()
        {
            if (vialFrame != null) Destroy(vialFrame);
            if (qiFilled != null) Destroy(qiFilled);
            if (qiEmpty != null) Destroy(qiEmpty);
            if (talismanMark != null) Destroy(talismanMark);
        }
        public void Flash(Vector3 point, Color color, string text, float duration)
        { effectAt = point; effectColor = color; effectMessage = text; effectUntil = Time.time + duration; }
        private void TextAt(Rect bounds, string value, int size, Color color)
        {
            if (labelStyle == null) labelStyle = new GUIStyle(GUI.skin.label);
            labelStyle.fontSize = size;
            labelStyle.fontStyle = FontStyle.Bold;
            labelStyle.normal.textColor = color;
            GUI.Label(bounds, value, labelStyle);
        }
        private void Bar(Rect rect, float filled, Color color)
        {
            GUI.color = new Color(0.06f, 0.13f, 0.15f, 0.9f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = color;
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width * Mathf.Clamp01(filled), rect.height), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }
        private void OnGUI()
        {
            if (Time.timeScale < 0.01f || YiRespawnGate.IsHidden(HeroController.instance)) return;
            // scalable Nine Sols-inspired reconstruction; original game files are not loaded
            float scale = Mathf.Max(0.8f, Screen.width / 1920f);
            Matrix4x4 oldUiMatrix = GUI.matrix;
            Color oldUiColor = GUI.color;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
            GUI.color = new Color(0.025f, 0.055f, 0.085f, 0.85f);
            GUI.DrawTexture(new Rect(20, 22, 660, 190), Texture2D.whiteTexture);
            GUI.color = Color.white;
            // Clean HUD: no redundant title, prompts or parry-state labels.

            // Original Nine Sols vial-outline art from the user's HUD atlas.
            // A turquoise fill sits under the transparent gold outline.
            // Eight charges are independent of Qi and update after pipe use/rest.
            for (int i = 0; i < MaxPipeCharges; i++)
            {
                float px = 41f + i * 63.5f;
                bool full = i < PipeCharges;
                GUI.color = full ? new Color(0.38f, 1f, 0.84f, 1f) :
                    new Color(0.16f, 0.27f, 0.28f, 0.9f);
                GUI.DrawTexture(new Rect(px + 7f, 58f, 32f, 22f), Texture2D.whiteTexture);
                GUI.color = full ? Color.white : new Color(0.65f, 0.65f, 0.65f, 0.6f);
                if (vialFrame != null)
                    GUI.DrawTexture(new Rect(px, 36f, 47f, 52f), vialFrame,
                                    ScaleMode.ScaleToFit, true);
                else
                {
                    // Fallback if the new Assets folder wasn't copied yet.
                    GUI.DrawTexture(new Rect(px + 6f, 83f, 35f, 3f), Texture2D.whiteTexture);
                    GUI.DrawTexture(new Rect(px + 6f, 56f, 3f, 30f), Texture2D.whiteTexture);
                    GUI.DrawTexture(new Rect(px + 38f, 56f, 3f, 30f), Texture2D.whiteTexture);
                    GUI.DrawTexture(new Rect(px + 6f, 53f, 35f, 3f), Texture2D.whiteTexture);
                }
            }
            GUI.color = Color.white;
            float hpFraction = MaxHp > 0 ? (float)CurrentHp / MaxHp : 0f;
            Bar(new Rect(40, 99, 585, 24), hpFraction, new Color(0.29f, 0.98f, 0.73f));
            if (InternalDamage > 0f && hpFraction > 0f)
            {
                GUI.color = new Color(0.70f, 0.16f, 0.21f, 0.90f);
                GUI.DrawTexture(new Rect(40 + 585 * hpFraction * (1f - InternalDamage),
                    99, 585 * hpFraction * InternalDamage, 24), Texture2D.whiteTexture);
                GUI.color = Color.white;
            }
            GUI.color = new Color(0.06f, 0.20f, 0.19f, 0.8f);
            for (int i = 1; i < 8; i++)
                GUI.DrawTexture(new Rect(40 + 585f * i / 8f, 99, 2f, 24), Texture2D.whiteTexture);
            GUI.color = Color.white;
            // Vial sprites show Pipe charges, while the green bar shows HP.
            // No redundant numerical labels are needed.
            // Original Nine Sols Qi-insignia inside circular gold-outlined slots.
            // Separate from the rectangular medicine vials above the HP bar.
            for (int i = 0; i < MaxChi; i++)
            {
                bool charged = i < Chi;
                Texture2D icon = charged ? qiFilled : qiEmpty;
                if (icon != null)
                {
                    GUI.color = Color.white;
                    GUI.DrawTexture(new Rect(42f + i * 58f, 143f, 44f, 44f), icon,
                        ScaleMode.ScaleToFit, true);
                }
                else
                {
                    GUI.color = charged ? new Color(0.38f, 1f, 0.75f) : new Color(0.8f, 0.72f, 0.48f);
                    GUI.DrawTexture(new Rect(54f + i * 58f, 156f, 20f, 20f), Texture2D.whiteTexture);
                }
            }
            GUI.color = Color.white;
            // The sigil follows the marked enemy until detonation or death.
            if (MarkedEnemy != null && MarkedEnemy.hp > 0 && talismanMark != null && Camera.main != null)
            {
                Vector3 sigilScreen = Camera.main.WorldToScreenPoint(
                    MarkedEnemy.transform.position + new Vector3(0f, 0.72f, 0f));
                if (sigilScreen.z > 0f)
                {
                    float sx = sigilScreen.x / scale;
                    float sy = (Screen.height - sigilScreen.y) / scale;
                    float wobble = 1f + 0.045f * Mathf.Sin(Time.unscaledTime * 8f);
                    float sw = 94f * wobble, sh = 72f * wobble;
                    GUI.color = new Color(1f, 1f, 1f, 0.96f);
                    GUI.DrawTexture(new Rect(sx - sw * 0.5f, sy - sh * 0.5f, sw, sh),
                        talismanMark, ScaleMode.ScaleToFit, true);
                    GUI.color = Color.white;
                }
            }
            // Only the resources belong inside the main HUD.
            // Charging feedback is drawn beside the actual character instead.
            if (NodePlaced && Camera.main != null)
            {
                Vector3 q = Camera.main.WorldToScreenPoint(NodePosition);
                if (q.z > 0f)
                {
                    float nx = q.x / scale, ny = (Screen.height - q.y) / scale;
                    TextAt(new Rect(nx-60, ny-90, 180, 40), "ROOT NODE", 20, Color.cyan);
                    TextAt(new Rect(nx-65, ny-60, 210, 30), "E: gear / rest", 15, Color.white);
                }
            }
            if (GearMenu)
            {
                GUI.color = new Color(0.04f, 0.08f, 0.11f, 0.97f);
                GUI.DrawTexture(new Rect(500, 230, 650, 330), Texture2D.whiteTexture);
                GUI.color = Color.white;
                TextAt(new Rect(530, 250, 570, 50), "ROOT NODE — EQUIPMENT", 29, Color.cyan);
                TextAt(new Rect(530, 315, 550, 50), "1 Standard    2 Defensive    3 Aggressive", 20, Color.white);
                TextAt(new Rect(530, 365, 500, 45), "Selected loadout: " + SelectedGear, 24, Color.yellow);
                TextAt(new Rect(530, 413, 580, 38), "AZURE BOW: " + BowName + "   Sand " + AzureSand + "/5", 18, Color.cyan);
                TextAt(new Rect(530, 462, 565, 55), "K: change bow | T: refill | 1–3: gear | E/B: close", 17, Color.white);
            }
            if (IsTalismanCharging && Camera.main != null)
            {
                Vector3 chargePoint = Camera.main.WorldToScreenPoint(
                    HeroController.instance != null ? HeroController.instance.transform.position : Vector3.zero);
                if (chargePoint.z > 0f)
                {
                    float ex = chargePoint.x / scale;
                    float ey = (Screen.height - chargePoint.y) / scale;
                    // Five Yi-style Qi circles sweep from upper-left to upper-right.
                    // Count advances while G is held, no floating numerical text.
                    for (int i = 0; i < 5; i++)
                    {
                        float angle = (165f - i * 37.5f) * Mathf.Deg2Rad;
                        float px = ex + 92f * Mathf.Cos(angle);
                        float py = ey - 55f - 65f * Mathf.Sin(angle);
                        float pulse = 1f + (i < TalismanChargeCount ?
                            0.07f * Mathf.Sin(Time.unscaledTime * 7f + i * 0.9f) : 0f);
                        float size = 30f * pulse;
                        Texture2D ringArt = i < TalismanChargeCount ? qiFilled : qiEmpty;
                        if (ringArt == null) continue;
                        Matrix4x4 beforeSpin = GUI.matrix;
                        Color beforeColor = GUI.color;
                        try
                        {
                            if (i < TalismanChargeCount)
                                GUIUtility.RotateAroundPivot(Time.unscaledTime * 70f + i * 20f,
                                    new Vector2(px, py));
                            GUI.color = i < TalismanChargeCount ? Color.white :
                                new Color(0.65f, 0.82f, 0.78f, 0.35f);
                            GUI.DrawTexture(new Rect(px-size/2f, py-size/2f, size, size),
                                ringArt, ScaleMode.ScaleToFit, true);
                        }
                        finally { GUI.matrix = beforeSpin; GUI.color = beforeColor; }
                    }
                }
            }
            // World-space parry impact and restrained screen tint. No combat labels.
            if (Time.unscaledTime < parryPulseUntil && Camera.main != null &&
                HeroController.instance != null)
            {
                float p = Mathf.Clamp01((Time.unscaledTime - parryPulseStart) / 0.31f);
                // No full-screen tint: parry feedback stays local to Yi.
                Vector3 q = Camera.main.WorldToScreenPoint(HeroController.instance.transform.position);
                if (q.z > 0f)
                {
                    float cx = q.x / scale, cy = (Screen.height - q.y) / scale;
                    float diameter = 62f + p * 74f;
                    GUI.color = new Color(parryPulseColor.r, parryPulseColor.g,
                        parryPulseColor.b, 0.58f * (1f - p));
                    Texture2D ring = qiFilled != null ? qiFilled : qiEmpty;
                    if (ring != null) GUI.DrawTexture(new Rect(cx - diameter / 2,
                        cy - diameter / 2, diameter, diameter), ring, ScaleMode.ScaleToFit, true);
                }
                GUI.color = Color.white;
            }
            GUI.matrix = oldUiMatrix;
            GUI.color = oldUiColor;
        }
    }
}
