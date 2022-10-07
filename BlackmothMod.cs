﻿using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GlobalEnums;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using Modding;
using UnityEngine;
using static FSMUtility;

namespace BlackmothMod
{
    public class Blackmoth : Mod
    {
        public static Blackmoth Instance;

        public override string GetVersion() => "2.1.0";

        public override void Initialize()
        {

            Instance = this;
            Instance.Log("Blackmoth initializing!");
            
            
            ModHooks.NewGameHook += GetReferences;
            ModHooks.AfterSavegameLoadHook += GetReferences;
            
            ModHooks.LanguageGetHook += Descriptions;

            ModHooks.HeroUpdateHook += Update;
            ModHooks.DashVectorHook += CalculateDashVelocity;
            ModHooks.DashPressedHook += CheckForDash;
            ModHooks.TakeDamageHook += InstanceOnTakeDamageHook;
            On.HeroController.LookForQueueInput += HeroControllerOnLookForQueueInput;
            On.HealthManager.TakeDamage += SetDamages;

            // Init dictionaries to stop nullRef.
            InitializeDictionaries();
            Instance.Log("Blackmoth initialized!");
        }

        #region Setting up player references for loading saves and creating new saves.

        private void GetReferences()
        {
            GameManager.instance.StartCoroutine(ConfigureHero());
        }

        private void GetReferences(SaveGameData data)
        {
            GameManager.instance.StartCoroutine(ConfigureHero());
        }
        
        IEnumerator ConfigureHero()
        {
            while (HeroController.instance == null)
                yield return new WaitForEndOfFrame();
            
            _hc = HeroController.instance;
            
            if (_sharpShadow == null || !_sharpShadow.CompareTag("Sharp Shadow"))
            {
                foreach (GameObject gameObject in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    if (gameObject != null && gameObject.CompareTag("Sharp Shadow"))
                    {
                        _sharpShadow = gameObject;
                        _sharpShadowFsm = LocateFSM(_sharpShadow, "damages_enemy");
                        LogDebug($@"Found Sharp Shadow: {_sharpShadow} - {_sharpShadowFsm}.");
                    }
                }
            }
            
            if (_heroRigidbody2D == null) _heroRigidbody2D = HeroController.instance.GetComponent<Rigidbody2D>();

            if (_heroAnimator == null) _heroAnimator = HeroController.instance.GetComponent<tk2dSpriteAnimator>();

            if(_heroActions == null) _heroActions = GameManager.instance.inputHandler.inputActions;
        }

        #endregion

        #region Handling inputs.

        private void HeroControllerOnLookForQueueInput(On.HeroController.orig_LookForQueueInput orig, HeroController self)
        {
            if (PlayerData.instance.GetBool("equippedCharm_35") && GameManager.instance.inputHandler.inputActions.dash.IsPressed)
            {
                CheckForDash();
            }
            orig(self);
        }
        
        private void Update()
        {
            RefreshTimers();

            if (_antiTurboDashFrames > 0 && _dashInvulTimer <= 0)
            {
                _antiTurboDashFrames--;
            }
            if (_dashInvulTimer > 0)
            {
                HeroController.instance.cState.invulnerable = true;
            }
            else if (_dashInvulTimer == 0 && _oldDashInvulTimer > 0)
            {
                _oldDashInvulTimer = 0;
                HeroController.instance.cState.invulnerable = false;
                // Set to the minimum number of frames between dashes. By default. 1 + however long one tick is rounded
                // down. I think this is the minimum possible # of frames that will still prevent turbo abuse for
                // invulnerability. You can still use the turbo button to go really fast though.
                _antiTurboDashFrames = 1 + (int)(Time.fixedDeltaTime / Time.deltaTime);
                LogDebug("Anti-turbo set to " + _antiTurboDashFrames);
            }

            if (HeroController.instance.cState.onGround && _dashCount > 1) _dashCount = 0;
        }
        
        private void RefreshTimers()
        {
            if (_dashCooldown > 0)
            {
                _dashCooldown -= Time.deltaTime;
            }
            else _dashCooldown = 0;
            if (_dashInvulTimer > 0)
            {
                _oldDashInvulTimer = _dashInvulTimer;
                _dashInvulTimer -= Time.deltaTime;
            }
            else _dashInvulTimer = 0;

            if (_hitStun > 0)
            {
                _hitStun -= Time.deltaTime;
            }
            else _hitStun = 0;
        }

        #endregion

        #region Handling the dash mechanic.

        private bool CheckForDash()
        {
            if (_antiTurboDashFrames > 0)
                return true;
            
            _dashCooldownStart = HeroController.instance.DASH_COOLDOWN_CH * 0.4f;
            _dashCooldownHasDash = HeroController.instance.DASH_COOLDOWN_CH * 0.1f;
            
            GetPrivateField("dashQueueSteps").SetValue(HeroController.instance, 0);
            GetPrivateField("dashQueuing").SetValue(HeroController.instance, false);
            
            HeroActions direction = GameManager.instance.inputHandler.inputActions;
            
            if (PlayerData.instance.GetBool("equippedCharm_35"))
            {
                Log("Got to grubber");
                GetPrivateField("dashCooldownTimer").SetValue(HeroController.instance, 0f);
                GetPrivateField("shadowDashTimer").SetValue(HeroController.instance, 0f);
            }

            if (direction.up.IsPressed)
            {
                _dashDirection.y = 1;
            }
            else if (direction.down.IsPressed && !HeroController.instance.cState.onGround)
            {
                _dashDirection.y = -1;
            }
            else
            {
                _dashDirection.y = 0;
            }

            if (direction.right.IsPressed)
            {
                _dashDirection.x = 1;
            }
            else if (direction.left.IsPressed)
            {
                _dashDirection.x = -1;
            }
            else if (_dashDirection.y == 0)
            {
                _dashDirection.x = HeroController.instance.cState.facingRight ? 1 : -1;
            }
            else
            {
                _dashDirection.x = 0;
            }
            LogDebug($@"Dash direction: {_dashDirection}");
            if (!PlayerData.instance.GetBool("hasDash"))
            {
                _dashDirection.y = 0;
                _dashDirection.x = HeroController.instance.cState.facingRight ? 1 : -1;
            }
            
            DoDash();

            // Fixes TC problem where after tink sharp shadow is broken
            _sharpShadowFsm.SetState("Idle");

            return true;
        }
        
        private Vector2 CalculateDashVelocity(Vector2 change)
        {
            float num;
            Vector2 ret;
            if (PlayerData.instance.GetBool("equippedCharm_16"))
            {
                if (!PlayerData.instance.GetBool("hasDash"))
                {
                    if (PlayerData.instance.GetBool("equippedCharm_16") && HeroController.instance.cState.shadowDashing)
                    {
                        num = HeroController.instance.DASH_SPEED_SHARP;
                    }
                    else
                    {
                        num = HeroController.instance.DASH_SPEED;
                    }
                }
                else if (PlayerData.instance.GetBool("equippedCharm_16") && HeroController.instance.cState.shadowDashing)
                {
                    num = HeroController.instance.DASH_SPEED_SHARP * 1.2f;
                }
                else
                {
                    num = HeroController.instance.DASH_SPEED * 1.2f;
                }
                if (PlayerData.instance.GetBool("equippedCharm_18"))
                {
                    num *= 1.2f;
                }
                if (PlayerData.instance.GetBool("equippedCharm_13"))
                {
                    num *= 1.5f;
                }
                if (_dashDirection.y == 0)
                {
                    if (HeroController.instance.cState.facingRight)
                    {
                        if (HeroController.instance.CheckForBump(CollisionSide.right))
                        {
                            ret = Time.deltaTime * new Vector2(num, (float)GetPrivateField("BUMP_VELOCITY_DASH").GetValue(HeroController.instance));
                        }
                        else
                        {
                            ret = Time.deltaTime * new Vector2(num, 0f);
                        }
                    }
                    else if (HeroController.instance.CheckForBump(CollisionSide.left))
                    {
                        ret = Time.deltaTime * new Vector2(-num, (float)GetPrivateField("BUMP_VELOCITY_DASH").GetValue(HeroController.instance));
                    }
                    else
                    {
                        ret = Time.deltaTime * new Vector2(-num, 0f);
                    }
                }
                else if (_dashDirection.x == 0)
                {
                    ret = Time.deltaTime * num * _dashDirection;
                }
                else
                {
                    ret = Time.deltaTime * (num / Mathf.Sqrt(2)) * _dashDirection;
                }
                _heroRigidbody2D.position = _heroRigidbody2D.position + ret;
                return (ret * 0.001f);
            }

            if (!PlayerData.instance.GetBool("hasDash"))
            {
                num = PlayerData.instance.GetBool("equippedCharm_16") && HeroController.instance.cState.shadowDashing
                    ? HeroController.instance.DASH_SPEED_SHARP
                    : HeroController.instance.DASH_SPEED;
            }
            else if (PlayerData.instance.GetBool("equippedCharm_16") && HeroController.instance.cState.shadowDashing)
            {
                num = HeroController.instance.DASH_SPEED_SHARP * 1.2f;
            }
            else
            {
                num = HeroController.instance.DASH_SPEED * 1.2f;
            }
            if (PlayerData.instance.GetBool("equippedCharm_18"))
            {
                num *= 1.2f;
            }
            if (PlayerData.instance.GetBool("equippedCharm_13"))
            {
                num *= 1.5f;
            }
            if (_dashDirection.y == 0)
            {
                if (HeroController.instance.cState.facingRight)
                {
                    ret = HeroController.instance.CheckForBump(CollisionSide.right) ? new Vector2(num, (float)GetPrivateField("BUMP_VELOCITY_DASH").GetValue(HeroController.instance)) : new Vector2(num, 0f);
                }
                else
                {
                    ret = HeroController.instance.CheckForBump(CollisionSide.left)
                        ? new Vector2(-num,
                            (float)GetPrivateField("BUMP_VELOCITY_DASH").GetValue(HeroController.instance))
                        : new Vector2(-num, 0f);
                }
            }
            else
                ret = _dashDirection.x == 0 ? num * _dashDirection : (num / Mathf.Sqrt(2)) * _dashDirection;

            return ret;
        }
        
        void DoDash()
        {
            if (HeroController.instance.hero_state != ActorStates.no_input
                && HeroController.instance.hero_state != ActorStates.hard_landing
                && HeroController.instance.hero_state != ActorStates.dash_landing
                && _dashCooldown <= 0f
                && !HeroController.instance.cState.dashing && !HeroController.instance.cState.backDashing
                && (!HeroController.instance.cState.attacking
                    || (float)GetPrivateField("attack_time").GetValue(HeroController.instance) >= HeroController.instance.ATTACK_RECOVERY_TIME)
                && !HeroController.instance.cState.preventDash
                && (HeroController.instance.cState.onGround
                    || !AirDashed()
                    || HeroController.instance.cState.wallSliding)
                || PlayerData.instance.GetBool("equippedCharm_35"))
            {
                _sharpShadowVolume = PlayerData.instance.GetBool("equippedCharm_35") ? 0.1f : 1f;
                if ((!HeroController.instance.cState.onGround || _dashDirection.y != 0) && !HeroController.instance.inAcid && !HeroController.instance.playerData.equippedCharm_32)
                {
                    GetPrivateField("airDashed").SetValue(HeroController.instance, true);
                }
                if (PlayerData.instance.GetBool("hasShadowDash"))
                {
                    HeroController.instance.AddMPChargeSpa(3);
                }


                InvokePrivateMethod("ResetAttacksDash");
                InvokePrivateMethod("CancelBounce");
                ((HeroAudioController)GetPrivateField("audioCtrl").GetValue(HeroController.instance)).StopSound(HeroSounds.FOOTSTEPS_RUN);
                ((HeroAudioController)GetPrivateField("audioCtrl").GetValue(HeroController.instance)).StopSound(HeroSounds.FOOTSTEPS_WALK);
                ((HeroAudioController)GetPrivateField("audioCtrl").GetValue(HeroController.instance)).StopSound(HeroSounds.DASH);
                InvokePrivateMethod("ResetLook");

                HeroController.instance.cState.recoiling = false;
                if (HeroController.instance.cState.wallSliding)
                {
                    HeroController.instance.FlipSprite();
                }
                else if (GameManager.instance.inputHandler.inputActions.right.IsPressed)
                {
                    HeroController.instance.FaceRight();
                }
                else if (GameManager.instance.inputHandler.inputActions.left.IsPressed)
                {
                    HeroController.instance.FaceLeft();
                }
                HeroController.instance.cState.dashing = true;
                GetPrivateField("dashQueueSteps").SetValue(HeroController.instance, 0);
                HeroController.instance.dashBurst.transform.localPosition = new Vector3(4.11f, -0.55f, 0.001f);
                HeroController.instance.dashBurst.transform.localEulerAngles = new Vector3(0f, 0f, 0f);
                HeroController.instance.dashingDown = false;

                if (HeroController.instance.playerData.equippedCharm_32)
                    _dashCooldown = 0;
                else
                {
                    _dashCooldown = HeroController.instance.playerData.hasDash ? _dashCooldownHasDash : _dashCooldownStart;
                }

                GetPrivateField("shadowDashTimer").SetValue(HeroController.instance, GetPrivateField("dashCooldownTimer").GetValue(HeroController.instance));
                HeroController.instance.proxyFSM.SendEvent("HeroCtrl-ShadowDash");
                HeroController.instance.cState.shadowDashing = true;
                ((AudioSource)GetPrivateField("audioSource").GetValue(HeroController.instance)).PlayOneShot(HeroController.instance.sharpShadowClip, _sharpShadowVolume);
                HeroController.instance.sharpShadowPrefab.SetActive(true);

                if (HeroController.instance.cState.shadowDashing)
                {
                    if (HeroController.instance.transform.localScale.x > 0f)
                    {
                        GetPrivateField("dashEffect").SetValue(HeroController.instance, HeroController.instance.shadowdashBurstPrefab.Spawn(new Vector3(HeroController.instance.transform.position.x + 5.21f, HeroController.instance.transform.position.y - 0.58f, HeroController.instance.transform.position.z + 0.00101f)));
                        ((GameObject)GetPrivateField("dashEffect").GetValue(HeroController.instance)).transform.localScale = new Vector3(1.919591f, ((GameObject)GetPrivateField("dashEffect").GetValue(HeroController.instance)).transform.localScale.y, ((GameObject)GetPrivateField("dashEffect").GetValue(HeroController.instance)).transform.localScale.z);
                    }
                    else
                    {
                        GetPrivateField("dashEffect").SetValue(HeroController.instance, HeroController.instance.shadowdashBurstPrefab.Spawn(new Vector3(HeroController.instance.transform.position.x - 5.21f, HeroController.instance.transform.position.y - 0.58f, HeroController.instance.transform.position.z + 0.00101f)));
                        ((GameObject)GetPrivateField("dashEffect").GetValue(HeroController.instance)).transform.localScale = new Vector3(-1.919591f, ((GameObject)GetPrivateField("dashEffect").GetValue(HeroController.instance)).transform.localScale.y, ((GameObject)GetPrivateField("dashEffect").GetValue(HeroController.instance)).transform.localScale.z);
                    }
                    HeroController.instance.shadowRechargePrefab.SetActive(true);
                    LocateFSM(HeroController.instance.shadowRechargePrefab, "Recharge Effect").SendEvent("RESET");
                    ParticleSystem ps = HeroController.instance.shadowdashParticlesPrefab.GetComponent<ParticleSystem>();
                    var em = ps.emission;
                    em.enabled = true;
                    HeroController.instance.shadowRingPrefab.Spawn(HeroController.instance.transform.position);
                }
                else
                {
                    HeroController.instance.dashBurst.SendEvent("PLAY");
                    ParticleSystem ps = HeroController.instance.dashParticlesPrefab.GetComponent<ParticleSystem>();
                    var em = ps.emission;
                    em.enabled = true;
                }
                if (HeroController.instance.cState.onGround && !HeroController.instance.cState.shadowDashing)
                {
                    GetPrivateField("dashEffect").SetValue(HeroController.instance, HeroController.instance.backDashPrefab.Spawn(HeroController.instance.transform.position));
                    ((GameObject)GetPrivateField("dashEffect").GetValue(HeroController.instance)).transform.localScale = new Vector3(HeroController.instance.transform.localScale.x * -1f, HeroController.instance.transform.localScale.y, HeroController.instance.transform.localScale.z);
                }
                _dashInvulTimer = 0.3f;
            }
        }
        
        bool AirDashed()
        {
            if (PlayerData.instance.GetBool("equippedCharm_31"))
            {
                if ((bool)GetPrivateField("airDashed").GetValue(HeroController.instance))
                {
                    if (_dashCount < 2)
                    {
                        _dashCount++;
                        return false;
                    }

                    return true;
                }

                return false;
            }

            return (bool)GetPrivateField("airDashed").GetValue(HeroController.instance);
        }

        #endregion

        #region Handling dealing and receiving damage.
        
        private void SetDamages(On.HealthManager.orig_TakeDamage orig, HealthManager owner, HitInstance 
            hit)
        {
            Log($@"Creating HitInstance {hit.AttackType}, {hit.Source}, {hit.DamageDealt}, {hit.Multiplier} for {owner.name}");
            _dashDamage = 5 + PlayerData.instance.GetInt("nailSmithUpgrades") * 4;
            float multiplier = 1;
            if (PlayerData.instance.GetBool("hasShadowDash"))
            {
                multiplier *= 2;
            }
            if (PlayerData.instance.GetBool("equippedCharm_25"))
            {
                multiplier *= 1.5f;
            }
            if (PlayerData.instance.GetBool("equippedCharm_6") && PlayerData.instance.GetInt("health") == 1)
            {
                multiplier *= 1.75f;
            }
            if (_oldDashDamage != _dashDamage)
            {
                LogDebug($@"[Blackmoth] Sharp Shadow Damage set to {_dashDamage}");
                _oldDashDamage = _dashDamage;
            }
            if (hit.AttackType == AttackTypes.SharpShadow || hit.Source.name == "SuperDash Damage" && PlayerData.instance.GetBool("defeatedNightmareGrimm"))
            {
                LogDebug($@"Setting damage for {owner.name}");
                hit.DamageDealt = _dashDamage;
                hit.AttackType = 0;
                hit.Multiplier = multiplier;
                hit.Direction = HeroController.instance.cState.facingRight ? 0 : 180;
                hit.MagnitudeMultiplier = PlayerData.instance.GetBool("equippedCharm_15") ? 2f : 0f;
            }
            else if (hit.AttackType == AttackTypes.Nail)
            {
                LogDebug($@"Setting damage for {owner.name}");
                hit.DamageDealt = 1;
            }
            LogDebug($@"Creating HitInstance {hit.AttackType}, {hit.Source}, {hit.DamageDealt}, {hit.Multiplier} for 
            {owner.name}");
            orig(owner, hit);
        }

        private int InstanceOnTakeDamageHook(ref int hazardtype, int damage)
        {
            if (hazardtype != (int) HazardType.SPIKES || HeroController.instance.cState.invulnerable) return damage;
            
            if (grubberOn) return 0;
            
            if (_hitStun > 0)
            {
                return 0;
            }

            _hitStun = 1.5f;
            return damage;
        }

        #endregion

        #region Fields, properties and privates.

        private FieldInfo GetPrivateField(string fieldName)
        {
            if (!_privateFields.ContainsKey(fieldName))
            {
                _privateFields.Add(fieldName,
                    HeroController.instance.GetType()
                        .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance));
            }
            return _privateFields[fieldName];
        }

        private void InvokePrivateMethod(string methodName)
        {
            if (!_privateMethods.ContainsKey(methodName))
            {
                _privateMethods.Add(methodName,
                    HeroController.instance.GetType()
                        .GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance));
            }
            _privateMethods[methodName]?.Invoke(HeroController.instance, new object[] { });
        }
        
        private MethodInfo GetPrivateMethod(string methodName)
        {
            if (!_privateMethods.ContainsKey(methodName))
            {
                _privateMethods.Add(methodName,
                    HeroController.instance.GetType()
                        .GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance));
            }
            return _privateMethods[methodName];
        }

        private Vector2 _dashDirection;
        private GameObject _sharpShadow;
        private PlayMakerFSM _sharpShadowFsm;
        private tk2dSpriteAnimator _heroAnimator;
        private int _oldDashDamage;
        private int _dashDamage;
        private float _dashCooldown;
        private float _dashCooldownStart;
        private float _dashCooldownHasDash;
        private float _oldDashInvulTimer;
        private float _dashInvulTimer;
        private float _sharpShadowVolume;
        private float _hitStun;
        private int _dashCount;
        private int _antiTurboDashFrames;
        public bool grubberOn;    
        private HeroActions _heroActions;
        private Rigidbody2D _heroRigidbody2D;
        private Dictionary<string, FieldInfo> _privateFields;
        private Dictionary<string, MethodInfo> _privateMethods;
        private Dictionary<KeyValuePair<int, string>, Dictionary<string, string>> _flavorDictionary;
        private HeroController _hc;

        #endregion
        
        #region Handling the in-game descriptions.

        private void InitializeDictionaries()
        {
            LogDebug("Initializing dictionaries.");
            _privateFields = new Dictionary<string, FieldInfo>();
            _privateMethods = new Dictionary<string, MethodInfo>();
            _flavorDictionary = new Dictionary<KeyValuePair<int, string>, Dictionary<string, string>>();
            Dictionary<string, string> ptbrUIDictionary = new Dictionary<string, string>
            {
                ["CHARM_DESC_13"] = @"Dado livremente pela Tribo dos Louva-deuses àqueles dignos de respeito.

Aumenta consideravelmente o alcance da esquiva do portador, permitindo-o atacar inimigos mais distantes.",

                ["CHARM_DESC_15"] = @"Formado de mantos de guerreiros caídos.
                            
Aumenta a força da esquiva do portador, fazendo inimigos recuarem mais quando atacados.",

                ["CHARM_DESC_16"] = @"Contém um feitiço proibido que transforma matéria em vazio.

Enquanto usa a esquiva, o corpo do portador poderá ir através de objetos sólidos.",

                ["CHARM_DESC_18"] = @"Aumenta o alcance da esquiva do portador, permitindo-o atacar inimigos mais de longe.",

                ["CHARM_DESC_25"] = @"Fortalece o portador, aumentando o dano que ele causa aos inimigos com sua esquiva.

Esse amuleto é frágil e quebrará se o portador for morto",

                ["CHARM_DESC_25_G"] = @"Fortalece o portador, aumentando o dano que ele causa nos inimigos com sua esquiva.

Esse amuleto é inquebrável.",

                ["CHARM_DESC_31"] = $@"Tem a semelhança de um inseto excêntrico conhecido apenas como {"O Mestre da Esquiva"}.

O portador será capaz de se esquivar mais, bem como esquivar-se em pleno ar. Perfeito para aqueles que querem se mover o mais rápido possível.",

                ["CHARM_DESC_32"] = @"Nascido de mantos descartados e imperfeitos que se uniram. Esses mantos ainda anseiam ser usados.

Permite o portador a golpear muito mais rapidamente com sua esquiva.",

                ["CHARM_DESC_35"] = @"Contém a gratidão de larvas que vão se mover para o próximo estágio de suas vidas. Confere uma força sagrada.

Permite ao portador ascender e se tornar o lendário Grubbermoth e voar pelos céus.",

                ["CHARM_NAME_16"] = @"Sombra do Vazio",

                ["CHARM_NAME_18"] = @"Esquiva Longa",

                ["CHARM_NAME_32"] = @"Esquiva Veloz",

                ["CHARM_NAME_35"] = @"Elegia do Grubbermoth.",

                ["INV_DESC_DASH"] = @"Manto feito com asas de mariposa. Permite ao usuário a esquivar-se em todas as direções.",

                ["INV_DESC_SHADOWDASH"] = @"Capa formada da substância do Abismo, o progenitor do Blackmoth. Permite ao usuário a ganhar ALMA e dar o dobro do dano enquanto se esquiva.",

                ["INV_DESC_SUPERDASH"] = PlayerData.instance.GetBool("defeatedNightmareGrimm") ? @"O núcleo de energia de um velho golem minerador enfeitado de um potente cristal. A energia pode ser usada para lançar o portador pra frente em velocidades perigosas.

Com o Pesadelo derrotado, o cristal revelou seu verdadeiro potencial, conferindo ao portador mais flexibilidade." : @"O núcleo de energia de um velho golem minerador enfeitado de um potente cristal. A energia pode ser usada para lançar o portador pra frente em velocidades perigosas.

Mesmo que seja bem poderoso, parece que um Pesadelo o impede de revelar seu verdadeiro potencial...",

                ["INV_NAME_SHADOWDASH"] = @"Manto de Blackmoth"
            };
            Dictionary<string, string> enUIDictionary = new Dictionary<string, string>
            {
                ["CHARM_DESC_13"] = @"Freely given by the Mantis Tribe to those they respect.

Greatly increases the range of the bearer's dash, allowing them to strike foes from further away.",

                ["CHARM_DESC_15"] = @"Formed from the cloaks of fallen warriors.
                            
Increases the force of the bearer's dash, causing enemies to recoil further when hit.",

                ["CHARM_DESC_16"] = @"Contains a forbidden spell that transforms matter into void.

When dashing, the bearer's body will be able to go through solid objects.",

                ["CHARM_DESC_18"] =
                    @"Strengthens the bearer, increasing the damage they deal to enemies with their dash.

This charm is fragile, and will break if its bearer is killed.",

                ["CHARM_DESC_25_G"] =
                    @"Strengthens the bearer, increasing the damage they deal to enemies with their dash.

This charm is ubreakable.",

                ["CHARM_DESC_31"] = $@"Bears the likeness of an eccentric bug known only as {"The Dashmaster"}.

The bearer will be able to dash more often as well as dash in midair. Perfect for those who want to move around as quickly as possible.",

                ["CHARM_DESC_32"] =
                    @"Born from imperfect, discarded cloaks that have fused together. The cloaks still long to be worn.

Allows the bearer to slash much more rapidly with their dash.",

                ["CHARM_DESC_35"] =
                    @"Contains the gratitude of grubs who will move to the next stage of their lives. Imbues weapons with a holy strength.

Allows bearer to ascend and become the legendary Grubbermoth and fly away to the heavens.",

                ["CHARM_NAME_16"] = @"Void Shade",

                ["CHARM_NAME_18"] = @"Longdash",

                ["CHARM_NAME_32"] = @"Quick Dash",

                ["CHARM_NAME_35"] = @"Grubbermoth's Elegy",

                ["INV_DESC_DASH"] =
                    @"Cloak threaded with mothwing strands. Allows the wearer to dash in every direction.",

                ["INV_DESC_SHADOWDASH"] =
                    @"Cloak formed from the substance of the Abyss, the progenitor of the Blackmoth. Allows the wearer to gain SOUL and deal double damage while dashing.",

                ["INV_DESC_SUPERDASH"] = PlayerData.instance.GetBool("defeatedNightmareGrimm")
                    ? @"The energy core of an old mining golem, fashioned around a potent crystal. The crystal's energy can be channeled to launch the bearer forward at dangerous speeds.

With the Nightmare defeated, the crystal has revealed its true potential, granting its wielder more flexibility."
                    : @"The energy core of an old mining golem, fashioned around a potent crystal. The crystal's energy can be channeled to launch the bearer forward at dangerous speeds.

Even though it's quite powerful, it seems as if a Nightmare is preventing it from unleashing its true potential...",

                ["INV_NAME_SHADOWDASH"] = @"Blackmoth Cloak"
            };
            Dictionary<string, string> rusUIDictionary = new Dictionary<string, string>
            {
                ["CHARM_DESC_13"] = @"Племя богомолов награждает подобными амулетами тех, кого они уважают.

Весьма ощутимо увеличивает дальность рывка, позволяя достать противников с большого расстояния.",

                ["CHARM_DESC_15"] = @"Создан из плащей поверженных воинов.
            
Увеличивает мощь рывка носителя, позволяя отбрасывать пораженных врагов назад с каждым ударом.",

                ["CHARM_DESC_16"] = @"Заключает в себе запретное заклятье, превращающее носителя в пустоту.

Во время рывка носитель может проходить сквозь твердые объекты.",

                ["CHARM_DESC_18"] =
                    @"УДает носителю больше силы, увеличивая урон, наносимый рывком.

Этот амулет очень хрупок и сломается при смерти владельца.",

                ["CHARM_DESC_25_G"] =
                    @"Дает носителю больше силы, увеличивая урон, наносимый рывком.

Этот амулет нельзя сломать.",

                ["CHARM_DESC_31"] = $@"Очень похож на чудаковатого жука, известного лишь как  {"Трюкач"}.

Носитель может чаще передвигаться рывками и даже делать рывок в воздухе. Отлично подходит для тех, кто хочет передвигаться быстрее.
",

                ["CHARM_DESC_32"] =
                    @"Создан из неблаговидных и забракованных плащей, сотканных воедино. Они все еще жаждут обрести хозяев.

Позволяет чаще наносить удары рывком.",

                ["CHARM_DESC_35"] =
                    @"Содержит в себе благодарность всех гусеничек, которым вы помогли перейти на следующую стадию жизни. Наполняет ваше оружие священной мощью.

Позволяет носителю вознестись и стать легендарным Гусельком, и улететь в рай.",

                ["CHARM_NAME_16"] = @"Тень Пустоты",

                ["CHARM_NAME_18"] = @"Длинный рывок",

                ["CHARM_NAME_32"] = @"Быстрый рывок",

                ["CHARM_NAME_35"] = @"Элегия Гуселька",

                ["INV_DESC_DASH"] =
                    @"Плащ с мотыльковой прострочкой. Позволяет носителю делать рывок в любую сторону.",

                ["INV_DESC_SHADOWDASH"] =
                    @"Плащ, сделанный из субстанции Бездны, прародителя Черного Мотылька. Позволяет носителю получать ДУШУ и наносить двойной урон при рывке.",

                ["INV_DESC_SUPERDASH"] = PlayerData.instance.GetBool("defeatedNightmareGrimm")
                    ? @"Силовое ядро старого шахтерского голема, в которое вделан крепкий кристалл. Сила кристалла позволяет носящему нестись вперед с умопомрачительной скоростью.

После уничтожения Кошмара кристалл раскрыл свой полный потенциал и дает носителю больше ловкости."
                    : @"Силовое ядро старого шахтерского голема, в которое вделан крепкий кристалл. Сила кристалла позволяет носящему нестись вперед с умопомрачительной скоростью.

Несмотря на то, что кристалл довольно силен, Кошмар до сих пор не дает раскрыть его полный потенциал.",

                ["INV_NAME_SHADOWDASH"] = @"Мантия Черного Мотылька"
            };
            Dictionary<string, string> zhUIDictionary = new Dictionary<string, string>
            {
                ["CHARM_DESC_13"] = @"由螳螂部落给予以表示他们的尊敬。

极大增长持有者的冲刺距离, 使他们可以在远距离击打敌人.",

                ["CHARM_DESC_15"] = @"阵亡战士的披风形成。
                            
增强持有者的冲刺力量, 使攻击时敌人退的更远.",

                ["CHARM_DESC_16"] = @"包含禁忌的虚空法术.

冲刺时，持有者的身体可以穿过固体",

                ["CHARM_DESC_18"] =
                    @"增强持有者的攻击力,使其冲刺能对敌人造成更多的伤害 .

这个护符十分脆弱，如果它的持有者被杀死将会被破坏.",

                ["CHARM_DESC_25_G"] =
                    @"增强持有者的攻击力,使其冲刺能对敌人造成更多的伤害.

这个护符不会损坏.",

                ["CHARM_DESC_31"] = $@"有着被称为{"冲刺大师"}的古怪虫子形象.

持有者能在半空中也更频繁地冲刺. 非常适合那些想要快速移动的虫子.",

                ["CHARM_DESC_32"] =
                    @"诞生于那些被融合的不完美的废弃披风.

允许持有者更快地使用冲刺.",

                ["CHARM_DESC_35"] =
                    @"包含将要步入生命的下一个阶段的幼虫们的感激。为武器灌注神圣的力量。

允许持有者上升并变成蛾子，飞向天堂",

                ["CHARM_NAME_16"] = @"虚空之影",

                ["CHARM_NAME_18"] = @"修长披风",

                ["CHARM_NAME_32"] = @"快速冲刺",

                ["CHARM_NAME_35"] = @"蜕蛾挽歌",

                ["INV_DESC_DASH"] =
                    @"蛾翅线编织成的披风。允许穿戴者项任意方向冲刺",

                ["INV_DESC_SHADOWDASH"] =
                    @"由深渊的物质形成的披风, 黑蛾的祖先。 允许穿戴着在冲刺时获取灵魂并造成双倍伤害",

                ["INV_DESC_SUPERDASH"] = PlayerData.instance.GetBool("defeatedNightmareGrimm")
                    ? @"一个古老挖矿魔像的能量核心，以一块强力的水晶塑成。水晶的能量可以被引导，能将持有者以危险的速度向前发射出去。

击败梦魇后，它释放了真正的力量，使使用者更加灵活"
                    : @"一个古老挖矿魔像的能量核心，以一块强力的水晶塑成。水晶的能量可以被引导，能将持有者以危险的速度向前发射出去。

尽管它很强大, 看起来梦魇阻碍它释放它真正的能力",

                ["INV_NAME_SHADOWDASH"] = @"黑蛾披风"
            };
            Dictionary<string, string> ptbrPromptDictionary = new Dictionary<string, string>
            {
                ["NAILSMITH_UPGRADE_1"] = "Pagar Geo para fortalecer a esquiva?",

                ["NAILSMITH_UPGRADE_2"] = "Dar Minério Pálido e Geo para fortalecer a esquiva?",

                ["NAILSMITH_UPGRADE_3"] = "Dar doi Minérios Pálido e Geo para fortalecer a esquiva?",

                ["NAILSMITH_UPGRADE_4"] = "Dar trê Minérios Pálido e Geo para fortalecer a esquiva?",

                ["GET_SHADOWDASH_2"] = "Use o manto para atravessar as costuras na realidade e pegar ALMA do espaço intermediário.",

                ["GET_SHADOWDASH_1"] = "para se esquivar enquanto coleta ALMA do ambiente.",

                ["GET_DASH_2"] = "Use o manto para esquivar-se rapidamente através do ar em todas as direções.",

                ["GET_DASH_1"] = "e qualquer direção para esquivar-se naquela direção."
            };
            Dictionary<string, string> enPromptDictionary = new Dictionary<string, string>
            {
                ["NAILSMITH_UPGRADE_1"] = "Pay Geo to strengthen dash?",

                ["NAILSMITH_UPGRADE_2"] = "Give Pale Ore and Geo to strengthen dash?",

                ["NAILSMITH_UPGRADE_3"] = "Give two Pale Ore and Geo to strengthen dash?",

                ["NAILSMITH_UPGRADE_4"] = "Give three Pale Ore and Geo to strengthen dash?",

                ["GET_SHADOWDASH_2"] = "Use the cloak to dash through the seams in reality and gather SOUL from the space in-between.",

                ["GET_SHADOWDASH_1"] = "to dash while gathering SOUL from the environment.",

                ["GET_DASH_2"] = "Use the cloak to dash quickly through the air in all directions.",

                ["GET_DASH_1"] = "while holding any direction to dash in that direction."
            };
            Dictionary<string, string> rusPromptDictionary = new Dictionary<string, string>
            {
                ["NAILSMITH_UPGRADE_1"] = "Заплатить Гео, чтобы усилить рывок?",

                ["NAILSMITH_UPGRADE_2"] = "Отдать одну Бледную руду и Гео для усиления рывка?",

                ["NAILSMITH_UPGRADE_3"] = "Отдать две Бледные руды и Гео для усиления рывка?",

                ["NAILSMITH_UPGRADE_4"] = "Отдать три Бледные руды и Гео для усиления рывка?",

                ["GET_SHADOWDASH_2"] = "Используйте плащ, чтобы проходить через щели в реальности и получать ДУШУ с пространства внутри.",

                ["GET_SHADOWDASH_1"] = "для рывка, пока получаете ДУШУ от окружения.",

                ["GET_DASH_2"] = "Используйте плащ, чтобы делать рывок в любом направлении.",

                ["GET_DASH_1"] = "пока держите кнопку любого направления для рывка в этом направлении."
            };
            Dictionary<string, string> zhPromptDictionary = new Dictionary<string, string>
            {
                ["NAILSMITH_UPGRADE_1"] = "支付吉欧来加强冲刺？",

                ["NAILSMITH_UPGRADE_2"] = "用苍白矿石和吉欧来加强冲刺？",

                ["NAILSMITH_UPGRADE_3"] = "用两个苍白矿石和吉欧来加强冲刺？",

                ["NAILSMITH_UPGRADE_4"] = "用两个苍白矿石和吉欧来加强冲刺？",

                ["GET_SHADOWDASH_2"] = "使用披风来穿过现实的细缝和并在虚实之间获取灵魂。",

                ["GET_SHADOWDASH_1"] = "在冲刺时从周围环境获取灵魂.",

                ["GET_DASH_2"] = "使用披风快速在空中任意方向冲刺。",

                ["GET_DASH_1"] = "按对应方向键以冲刺。"
            };
            _flavorDictionary.Add(new KeyValuePair<int, string>(147, "UI"), ptbrUIDictionary);
            _flavorDictionary.Add(new KeyValuePair<int, string>(44, "UI"), enUIDictionary);
            _flavorDictionary.Add(new KeyValuePair<int, string>(154, "UI"), rusUIDictionary);
            _flavorDictionary.Add(new KeyValuePair<int, string>(199, "UI"), zhUIDictionary);
            _flavorDictionary.Add(new KeyValuePair<int, string>(147, "Prompts"), ptbrPromptDictionary);
            _flavorDictionary.Add(new KeyValuePair<int, string>(44, "Prompts"), enPromptDictionary);
            _flavorDictionary.Add(new KeyValuePair<int, string>(154, "Prompts"), rusPromptDictionary);
            _flavorDictionary.Add(new KeyValuePair<int, string>(199, "Prompts"), zhPromptDictionary);
            LogDebug("Finished initializing dictionaries.");
        }
        
        string Descriptions(string key, string sheet,string orig)
        {
            string ret = orig;
            int lang = (int)Language.Language.CurrentLanguage();
            KeyValuePair<int, string> langSheet = new KeyValuePair<int, string>(lang, sheet);
            if (!_flavorDictionary.ContainsKey(langSheet)) return ret;
            return _flavorDictionary[langSheet].ContainsKey(key) ? _flavorDictionary[langSheet][key] : ret;
        }

        #endregion
    }
}