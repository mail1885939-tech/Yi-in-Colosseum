using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace Yi_in_Coloseum
{
    // v1.4.3: Authentic-only sound slots. Deliberately NEVER reads the old
    // synthesized Audio/*.wav test pack (a possible cause of continuous buzzing).
    // Put locally extracted Nine Sols SFX in Audio/NineSols/*.wav instead.
    public sealed class YiAudio : MonoBehaviour
    {
        private readonly Dictionary<string, AudioClip> sounds = new Dictionary<string, AudioClip>();
        private readonly Dictionary<string, float> played = new Dictionary<string, float>();
        private AudioSource speaker;
        private float suppressVanillaHitUntil;
        private AudioSource[] cachedHeroSources;
        private float lastVanillaScan;
        private HeroController hero;
        private Rigidbody2D movement;
        private bool previousGrounded;
        private bool previousWall;
        private bool previousDash;
        private float nextStep;
        private int stepNumber;
        private int attackStage;
        private float lastAttack = -100f;
        private bool enabledAudio = true;
        private float volume = 0.75f;
        private readonly Dictionary<string, float> perSound = new Dictionary<string, float>();
        private string perSoundFile;
        private string mixerFile;
        private int mixerIndex;
        private float mixerOverlayUntil;
        private readonly string[] mixerNames = {
            "combat", "parry", "bow", "talisman", "healing", "movement", "ui"
        };
        private readonly Dictionary<string, float> mixerLevels =
            new Dictionary<string, float>();
        private static readonly string[] required = {
            "attack_1", "attack_2", "attack_3", "parry_start", "parry_perfect",
            "parry_imperfect", "super_charge", "super_release", "bow_piercer",
            "bow_thunder", "bow_hunter", "bow_hit", "bow_no_ammo",
            "talisman_dash", "talisman_mark", "talisman_charge", "talisman_burst",
            "heal_begin", "heal_finish", "heal_cancel", "hurt", "death",
            "root_place", "root_rest", "menu_open", "menu_select",
            "footstep_1", "footstep_2", "jump", "land", "wall_contact", "dash"
        };

        private void Awake()
        {
            hero = GetComponent<HeroController>();
            movement = GetComponent<Rigidbody2D>();
            speaker = gameObject.AddComponent<AudioSource>();
            speaker.playOnAwake = false;
            speaker.loop = false;
            speaker.spatialBlend = 0f;
            speaker.dopplerLevel = 0f;
            speaker.volume = volume;
            speaker.priority = 110;

            string baseAudioFolder = Path.Combine(Path.GetDirectoryName(
                Assembly.GetExecutingAssembly().Location), "Audio");
            // No fallback to the previous synthesized clip directory.
            string folder = Path.Combine(baseAudioFolder, "NineSols");
            string control = Path.Combine(baseAudioFolder, "volume.txt");
            try
            {
                if (File.Exists(control))
                {
                    float number;
                    if (float.TryParse(File.ReadAllText(control).Trim(),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                        volume = Mathf.Clamp01(number);
                }
            }
            catch (Exception ex) { Debug.LogWarning("Yi audio volume: " + ex.Message); }
            speaker.volume = volume;
            mixerFile = Path.Combine(baseAudioFolder, "mixer.ini");
            perSoundFile = Path.Combine(baseAudioFolder, "sound_overrides.ini");
            LoadMixer();
            LoadPerSoundOverrides();

            int loaded = 0;
            for (int i = 0; i < required.Length; ++i)
            {
                string key = required[i];
                string filename = Path.Combine(folder, key + ".wav");
                if (!File.Exists(filename)) continue;
                try
                {
                    AudioClip clip = ReadMonoPcmWav(filename, key);
                    if (clip == null) continue;
                    sounds.Add(key, clip);
                    loaded++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("Yi sound '" + key + "' couldn't load: " + ex.Message);
                }
            }
            Debug.Log("YI_V143_REAL_ONLY " + loaded + "/" + required.Length +
                " Nine Sols WAV slots. Old synthesized WAV directory ignored.");
            if (loaded == 0) Debug.Log("Yi audio silent: put original SFX into Audio/NineSols. No synthetic fallback.");
        }

        private void LoadPerSoundOverrides()
        {
            // Optional per-event fine tuning. Values multiply their category level.
            foreach (string key in required) perSound[key] = 1f;
            perSound["parry_start"] = 0.36f;
            perSound["parry_perfect"] = 0.62f;
            perSound["parry_imperfect"] = 0.49f;
            perSound["super_charge"] = 0.46f;
            perSound["super_release"] = 0.65f;
            perSound["footstep_1"] = 0.88f;
            perSound["footstep_2"] = 0.88f;
            try
            {
                if (!File.Exists(perSoundFile)) { SavePerSoundOverrides(); return; }
                foreach (string line in File.ReadAllLines(perSoundFile))
                {
                    string[] parts = line.Split(new[] { '=' }, 2);
                    float value;
                    if (parts.Length == 2 && perSound.ContainsKey(parts[0].Trim()) &&
                        float.TryParse(parts[1].Trim(), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out value))
                        perSound[parts[0].Trim()] = Mathf.Clamp01(value);
                }
            }
            catch (Exception ex) { Debug.LogWarning("Yi per-sound mixer: " + ex.Message); }
        }
        private void SavePerSoundOverrides()
        {
            try
            {
                List<string> lines = new List<string>();
                lines.Add("# Yi v1.6.1 authentic sound gains. Multiply with mixer.ini groups.");
                foreach (string key in required)
                    lines.Add(key + "=" + perSound[key].ToString("0.00", CultureInfo.InvariantCulture));
                Directory.CreateDirectory(Path.GetDirectoryName(perSoundFile));
                File.WriteAllLines(perSoundFile, lines.ToArray());
            }
            catch (Exception ex) { Debug.LogWarning("Yi per-sound mixer save: " + ex.Message); }
        }

        private void LoadMixer()
        {
            foreach (string name in mixerNames) mixerLevels[name] = 0.65f;
            // Keep parry audible above movement, tame the loudest bow impacts.
            mixerLevels["movement"] = 0.72f;
            mixerLevels["parry"] = 0.38f;
            mixerLevels["combat"] = 0.70f;
            mixerLevels["bow"] = 0.60f;
            mixerLevels["talisman"] = 0.58f;
            mixerLevels["healing"] = 0.60f;
            mixerLevels["ui"] = 0.42f;
            try
            {
                if (!File.Exists(mixerFile)) { SaveMixer(); return; }
                foreach (string line in File.ReadAllLines(mixerFile))
                {
                    string clean = line.Trim();
                    if (clean.StartsWith("#") || !clean.Contains("=")) continue;
                    string[] parts = clean.Split(new char[] { '=' }, 2);
                    float value;
                    if (parts.Length == 2 && mixerLevels.ContainsKey(parts[0].Trim()) &&
                        float.TryParse(parts[1].Trim(), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out value))
                        mixerLevels[parts[0].Trim()] = Mathf.Clamp01(value);
                }
            }
            catch (Exception e) { Debug.LogWarning("Yi mixer: " + e.Message); }
        }
        private void SaveMixer()
        {
            try
            {
                List<string> lines = new List<string>();
                lines.Add("# Yi v1.6 audio mixer. Values 0.0 to 1.0. F1 chooses group, PageUp/PageDown adjusts.");
                foreach (string key in mixerNames)
                    lines.Add(key + "=" + mixerLevels[key].ToString("0.00", CultureInfo.InvariantCulture));
                Directory.CreateDirectory(Path.GetDirectoryName(mixerFile));
                File.WriteAllLines(mixerFile, lines.ToArray());
            }
            catch (Exception e) { Debug.LogWarning("Yi mixer save: " + e.Message); }
        }
        private string GroupFor(string key)
        {
            if (key.StartsWith("parry") || key.StartsWith("super")) return "parry";
            if (key.StartsWith("bow")) return "bow";
            if (key.StartsWith("talisman")) return "talisman";
            if (key.StartsWith("heal")) return "healing";
            if (key == "hurt" || key == "death" || key.StartsWith("attack")) return "combat";
            if (key == "jump" || key == "land" || key == "dash" ||
                key.StartsWith("footstep") || key == "wall_contact") return "movement";
            return "ui";
        }

        // Simple RIFF reader: no UnityWebRequest, no online dependencies, no loading per shot.
        private static AudioClip ReadMonoPcmWav(string filename, string key)
        {
            using (FileStream stream = File.OpenRead(filename))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF") return null;
                reader.ReadInt32(); // RIFF size
                if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE") return null;
                ushort format = 0, channels = 0, bits = 0;
                int sampleRate = 0;
                byte[] data = null;
                while (stream.Position + 8 <= stream.Length)
                {
                    string tag = Encoding.ASCII.GetString(reader.ReadBytes(4));
                    int length = reader.ReadInt32();
                    if (length < 0 || length > stream.Length - stream.Position) return null;
                    long next = stream.Position + length + (length & 1);
                    if (tag == "fmt " && length >= 16)
                    {
                        format = reader.ReadUInt16();
                        channels = reader.ReadUInt16();
                        sampleRate = reader.ReadInt32();
                        reader.ReadInt32(); // byte rate
                        reader.ReadUInt16(); // block alignment
                        bits = reader.ReadUInt16();
                    }
                    else if (tag == "data") data = reader.ReadBytes(length);
                    stream.Position = Math.Min(stream.Length, next);
                }
                if (format != 1 || (channels != 1 && channels != 2) || bits != 16 || sampleRate <= 0 ||
                    data == null || data.Length < 2 || data.Length > 3 * 1024 * 1024)
                {
                    Debug.LogWarning("Yi sound expects PCM 16-bit mono/stereo WAV: " + filename);
                    return null;
                }
                int count = data.Length / (channels * 2);
                float[] samples = new float[count];
                for (int i = 0; i < count; ++i)
                {
                    int start = i * channels * 2;
                    short left = (short)(data[start] | (data[start + 1] << 8));
                    if (channels == 1) samples[i] = left / 32768f;
                    else
                    {
                        short right = (short)(data[start + 2] | (data[start + 3] << 8));
                        samples[i] = (left + right) / 65536f;
                    }
                }
                AudioClip clip = AudioClip.Create("Yi_" + key, count, 1, sampleRate, false);
                if (!clip.SetData(samples, 0)) { UnityEngine.Object.Destroy(clip); return null; }
                return clip;
            }
        }

        public void Play(string key, float level = 1f)
        {
            if (!enabledAudio || speaker == null || Time.timeScale <= 0.001f) return;
            AudioClip clip;
            if (!sounds.TryGetValue(key, out clip) || clip == null) return;
            float previous;
            // One-shot deduplication for multi-target hits and rapid updates.
            if (played.TryGetValue(key, out previous) && Time.unscaledTime - previous < 0.065f) return;
            played[key] = Time.unscaledTime;
            float fine = perSound.ContainsKey(key) ? perSound[key] : 1f;
            // 20% quieter footsteps even if users have older mixer.ini settings.
            if (key == "footstep_1" || key == "footstep_2") fine *= 0.80f;
            speaker.PlayOneShot(clip,
                Mathf.Clamp01(level) * mixerLevels[GroupFor(key)] * fine);
        }

        // Selective, best-effort cancellation of the Knight's TAKE_HIT sound during
        // a successful parry. We never mute music, ambience, or other audio globally.
        // Exact behavior depends on the version of the game's audio controller.
        public void SuppressVanillaHitFor(float seconds)
        {
            suppressVanillaHitUntil = Mathf.Max(suppressVanillaHitUntil,
                Time.unscaledTime + Mathf.Max(0f, seconds));
            if (cachedHeroSources == null) CacheHeroAudioSources();
            StopKnownHitClips();
        }

        private void CacheHeroAudioSources()
        {
            if (hero == null) return;
            cachedHeroSources = hero.GetComponentsInChildren<AudioSource>(true);
            lastVanillaScan = Time.unscaledTime;
        }

        // The Knight's own steps can overlap Yi's. Stop only known footstep
        // clips attached to the hero; never globally mute music or ambience.
        private void SilenceKnightFootsteps()
        {
            if (cachedHeroSources == null) CacheHeroAudioSources();
            if (cachedHeroSources == null) return;
            foreach (AudioSource source in cachedHeroSources)
            {
                if (source == null || source == speaker || !source.isPlaying || source.clip == null) continue;
                string label = source.clip.name.ToLowerInvariant();
                if (label.Contains("footstep") || label.Contains("foot_step") ||
                    label.Contains("hero_run")) source.Stop();
            }
        }

        private void StopKnownHitClips()
        {
            if (cachedHeroSources == null) return;
            foreach (AudioSource source in cachedHeroSources)
            {
                if (source == null || source == speaker || !source.isPlaying || source.clip == null)
                    continue;
                string name = source.clip.name.ToLowerInvariant();
                // Only known hero-damage clips. Doesn't suppress unrelated music/sounds.
                if (name.Contains("hero_damage") || name.Contains("take_hit") ||
                    name.Contains("hero_take_hit") || name.Contains("hero_hurt"))
                    source.Stop();
            }
        }

        public void PlayAttack()
        {
            attackStage = Time.unscaledTime - lastAttack < 0.60f ? attackStage % 3 + 1 : 1;
            lastAttack = Time.unscaledTime;
            Play("attack_" + attackStage, 0.85f);
        }

        public void Bow(int mode)
        {
            Play(mode == 0 ? "bow_piercer" : mode == 1 ? "bow_thunder" : "bow_hunter");
        }

        private void Update()
        {
            if (hero == null) return;
            if (Time.timeScale > 0.01f)
            {
                if (Input.GetKeyDown(KeyCode.F1))
                {
                    mixerIndex = (mixerIndex + 1) % mixerNames.Length;
                    mixerOverlayUntil = Time.unscaledTime + 2.4f;
                }
                float change = Input.GetKeyDown(KeyCode.PageUp) ? 0.10f :
                    Input.GetKeyDown(KeyCode.PageDown) ? -0.10f : 0f;
                if (change != 0f)
                {
                    string category = mixerNames[mixerIndex];
                    mixerLevels[category] = Mathf.Clamp01(mixerLevels[category] + change);
                    mixerOverlayUntil = Time.unscaledTime + 2.4f;
                    SaveMixer();
                }
            }
            if (Time.unscaledTime < suppressVanillaHitUntil)
            {
                if (cachedHeroSources == null || Time.unscaledTime - lastVanillaScan > 0.15f)
                    CacheHeroAudioSources();
                StopKnownHitClips();
            }
            if (Input.GetKeyDown(KeyCode.F2))
            {
                enabledAudio = !enabledAudio;
                if (!enabledAudio && speaker != null) speaker.Stop();
                Debug.Log("Yi audio " + (enabledAudio ? "ON" : "OFF"));
            }
            if (Time.timeScale <= 0.01f) return;
            bool grounded = hero.cState.onGround;
            bool wall = hero.cState.wallSliding;
            bool dashNow = hero.cState.dashing;
            if (grounded && !previousGrounded) Play("land", 0.55f);
            if (!grounded && previousGrounded && movement != null && movement.velocity.y > 1f)
                Play("jump", 0.58f);
            if (wall && !previousWall) Play("wall_contact", 0.52f);
            if (dashNow && !previousDash) Play("dash", 0.55f);
            if (grounded && movement != null && Mathf.Abs(movement.velocity.x) > 0.9f)
                SilenceKnightFootsteps();
            if (grounded && movement != null && Mathf.Abs(movement.velocity.x) > 0.9f &&
                Time.unscaledTime >= nextStep)
            {
                float speed = Mathf.Abs(movement.velocity.x);
                SilenceKnightFootsteps();
                Play((stepNumber++ % 2 == 0) ? "footstep_1" : "footstep_2",
                    speed > 3.2f ? 0.72f : 0.56f);
                nextStep = Time.unscaledTime + (speed > 3.2f ? 0.21f : 0.35f);
            }
            previousGrounded = grounded;
            previousWall = wall;
            previousDash = dashNow;
        }

        private void OnGUI()
        {
            if (Time.unscaledTime >= mixerOverlayUntil || Time.timeScale < 0.01f) return;
            string key = mixerNames[mixerIndex];
            GUIStyle style = new GUIStyle(GUI.skin.label);
            style.fontSize = 18;
            style.fontStyle = FontStyle.Bold;
            style.normal.textColor = new Color(0.5f, 1f, 0.87f);
            GUI.Label(new Rect(Screen.width - 375, 55, 355, 38),
                "YI AUDIO  " + key.ToUpperInvariant() + "  " +
                Mathf.RoundToInt(mixerLevels[key] * 100f) + "%", style);
        }

        private void OnDestroy()
        {
            foreach (AudioClip clip in sounds.Values)
                if (clip != null) UnityEngine.Object.Destroy(clip);
            sounds.Clear();
            // AudioSource is a child component on the hero and will be cleaned up by Unity.
        }
    }
}
