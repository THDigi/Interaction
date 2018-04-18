using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Components;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.ModAPI;
using SpaceEngineers.Game.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.Entity.UseObject;
using VRage.Game.ModAPI;
using VRage.Input;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

namespace Digi.Interaction
{
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class InteractionMod : MySessionComponentBase
    {
        private const int WORKSHOP_ID = 652337022;
        private const int WORKSHOP_ID_DEV = 650441224;
        private const string LOCAL_FOLDER = "Interaction";
        private const string LOCAL_FOLDER_DEV = "Interaction.dev";

        public override void LoadData()
        {
            Log.SetUp("Animated Interaction", WORKSHOP_ID, "Interaction");
            instance = this;
        }

        public enum InteractionType
        {
            START,
            START_TARGET,
            END,
            END_TARGET,
            CLICK,
            TYPE,
            TAB,
            RC_ON,
            RC_OFF,
            CAMERA_ON,
            CAMERA_OFF,
            USE_BUTTON,
            USE_HOLD_ON,
            USE_HOLD_OFF,
        }

        public class InteractionEffect
        {
            public string animation = null;
            public MyFrameOption frameOption = MyFrameOption.StayOnLastFrame;
            public float timeScale = 1f;
            public string sound = null;
            public long delayTicks = 0;

            private MyAnimationCommand animCmd = new MyAnimationCommand()
            {
                PlaybackCommand = MyPlaybackCommand.Play,
                BlendOption = MyBlendOption.Immediate,
                BlendTime = 0.25f,
            };

            public InteractionEffect() { }

            public void PlayAnimation(IMyCharacter character, bool forceStayOnLastFrame = false)
            {
                var skinned = (MySkinnedEntity)character;

                if(skinned.UseNewAnimationSystem)
                {
                    character.TriggerCharacterAnimationEvent(animation, true);
                }
                else
                {
                    animCmd.AnimationSubtypeName = animation;
                    animCmd.TimeScale = timeScale;
                    animCmd.FrameOption = (forceStayOnLastFrame ? MyFrameOption.StayOnLastFrame : frameOption);
                    skinned.AddCommand(animCmd, true);
                }
            }
        }

        public class Interacted
        {
            public long target;
            public long time;

            public Vector3? offset = null;
            public bool remove = false;
            public byte skipGravity = SKIP_TICKS_GRAVITY;
            public bool inGravity = false;
            public Vector3 gravity;

            public List<CableJoint> cable = null;
            public bool cableAtFingers = false;

            public Interacted() { }

            public void Clear()
            {
                if(cable != null)
                {
                    foreach(var c in cable)
                    {
                        if(c.ents != null)
                        {
                            foreach(var e in c.ents)
                            {
                                e.Close();
                            }

                            c.ents = null;
                        }
                    }

                    cable = null;
                }
            }
        }

        public class CableJoint
        {
            public Vector3D position;
            public Vector3 velocity = Vector3.Zero;
            public Vector3 prevDir = Vector3.Zero;
            public List<MyEntity> ents = null;

            public CableJoint() { }
        }

        public static InteractionMod instance = null;

        public bool init = false;
        public bool isThisHostDedicated = false;

        public Settings settings = null;

        private IMyCharacter characterEntity = null;
        private bool unsupportedCharacter = false;

        private bool lcdShown = false;
        private bool lastInRC = false;
        private bool lastInCamera = false;
        private bool lastHoldingUse = false;
        private long startTime = 0;
        private long lastInteraction = 0;
        private MyTerminalPageEnum lastPage = MyTerminalPageEnum.None;
        private short skipPlanets = SKIP_TICKS_PLANETS;
        public string pathToMod = null;
        public string pathToCable = null;

        private Dictionary<long, Interacted> interactEntity = new Dictionary<long, Interacted>();
        private List<long> removeInteractEntity = new List<long>();

        private List<MyKeys> keys = new List<MyKeys>();
        private HashSet<MyKeys> prevKeys = new HashSet<MyKeys>();

        public List<MyPlanet> planets = new List<MyPlanet>();
        public Dictionary<long, IMyGravityProvider> gravityGenerators = new Dictionary<long, IMyGravityProvider>();

        private static readonly List<IMyPlayer> players = new List<IMyPlayer>(0);
        private static readonly HashSet<IMyEntity> ents = new HashSet<IMyEntity>();
        private static readonly List<MyEntity> detectableEnts = new List<MyEntity>();
        private static readonly List<IHitInfo> hits = new List<IHitInfo>();
        private static BoundingSphereD sphere = new BoundingSphereD(Vector3D.Zero, MyConstants.DEFAULT_INTERACTIVE_DISTANCE);

        private const ushort PACKET = 12004;
        private static readonly Encoding encode = Encoding.Unicode;
        private const char SEPARATOR = ' ';

        private const long DELAY_GRABCABLE = TimeSpan.TicksPerMillisecond * 200;
        private const long DELAY_CONNECT = TimeSpan.TicksPerMillisecond * 800;

        private const long DELAY_DISCONNECT = TimeSpan.TicksPerMillisecond * 300;
        private const long DELAY_PUTCABLE = TimeSpan.TicksPerMillisecond * 800;

        private const int PACKET_SOUND_DISTANCE = 100 * 100;
        private const int CABLE_DRAWDISTANCE_SQ = 1000 * 1000;

        private const byte SKIP_TICKS_GRAVITY = 10;
        private const short SKIP_TICKS_PLANETS = 600;

        private const string BONE_RIGHTHAND_INDEXTIP = "SE_RigR_Index_3";
        private const string BONE_LEFTARM_LCD = "SE_RigLForearm3";

        private static readonly Vector4 CABLE_COLOR = new Color(15, 15, 15).ToVector4();
        private static readonly MyStringId MATERIAL_WEAPONLASER = MyStringId.GetOrCompute("WeaponLaser");

        private readonly Dictionary<InteractionType, InteractionEffect> interactionEffect = new Dictionary<InteractionType, InteractionEffect>()
        {
            {InteractionType.START, new InteractionEffect()
                {
                    animation = "interactionstart",
                    sound = "PlayerLCDStart",
                    delayTicks = TimeSpan.TicksPerMillisecond * 400,
                }
            },

            {InteractionType.START_TARGET, new InteractionEffect()
                {
                    animation = "interactionstarttarget",
                    sound = "PlayerLCDStart",
                    delayTicks = TimeSpan.TicksPerMillisecond * 1200,
                }
            },

            {InteractionType.END, new InteractionEffect()
                {
                    animation = "interactionend",
                    sound = "PlayerLCDEnd",
                    frameOption = MyFrameOption.PlayOnce,
                }
            },

            {InteractionType.END_TARGET, new InteractionEffect()
                {
                    animation = "interactionendtarget",
                    sound = "PlayerLCDEnd",
                    frameOption = MyFrameOption.PlayOnce,
                }
            },

            {InteractionType.CLICK, new InteractionEffect()
                {
                    animation = "interactionclick",
                    sound = "PlayerLCDClick",
                    delayTicks = TimeSpan.TicksPerMillisecond * 250,
                }
            },

            {InteractionType.TYPE, new InteractionEffect()
                {
                    animation = "interactiontype",
                    sound = "PlayerLCDType",
                    delayTicks = TimeSpan.TicksPerMillisecond * 1000,
                }
            },

            {InteractionType.TAB, new InteractionEffect()
                {
                    animation = "interactiontab",
                    sound = "PlayerLCDTab",
                    delayTicks = TimeSpan.TicksPerMillisecond * 350,
                }
            },

            {InteractionType.RC_ON, new InteractionEffect()
                {
                    animation = "interactionrcon",
                }
            },

            {InteractionType.RC_OFF, new InteractionEffect()
                {
                    animation = "interactionrcoff",
                    frameOption = MyFrameOption.PlayOnce,
                    delayTicks = TimeSpan.TicksPerMillisecond * 300,
                }
            },

            {InteractionType.CAMERA_ON, new InteractionEffect()
                {
                    animation = "interactioncameraon",
                }
            },

            {InteractionType.CAMERA_OFF, new InteractionEffect()
                {
                    animation = "interactioncameraoff",
                    frameOption = MyFrameOption.PlayOnce,
                    delayTicks = TimeSpan.TicksPerMillisecond * 300,
                }
            },

            {InteractionType.USE_BUTTON, new InteractionEffect()
                {
                    animation = "interactionusebutton",
                    frameOption = MyFrameOption.PlayOnce,
                    delayTicks = TimeSpan.TicksPerMillisecond * 900,
                    timeScale = 0.8f,
                }
            },

            {InteractionType.USE_HOLD_ON, new InteractionEffect()
                {
                    animation = "interactionusebuttonon",
                    frameOption = MyFrameOption.StayOnLastFrame,
                    delayTicks = TimeSpan.TicksPerMillisecond * 900,
                    timeScale = 0.8f,
                }
            },

            {InteractionType.USE_HOLD_OFF, new InteractionEffect()
                {
                    animation = "interactionusebuttonoff",
                    frameOption = MyFrameOption.PlayOnce,
                    timeScale = 0.8f,
                }
            },
        };

        private readonly HashSet<string> useObjects = new HashSet<string>()
        {
            "MyUseObjectPanelButton",
            "MyUseObjectDoorTerminal",
            "MyUseObjectAdvancedDoorTerminal",
            "MyUseObjectAirtightDoors",
        };

        private readonly HashSet<string> useHoldObjects = new HashSet<string>()
        {
            "MyUseObjectMedicalRoom",
        };

        private string GetModPath(ulong workshopId, ulong workshopIdDev, string localFolder, string localFolderDev)
        {
            foreach(var mod in MyAPIGateway.Session.Mods)
            {
                if(mod.PublishedFileId == 0 ? (mod.Name == localFolder || mod.Name == localFolderDev) : (mod.PublishedFileId == workshopId || mod.PublishedFileId == workshopIdDev))
                    return Path.Combine(MyAPIGateway.Utilities.GamePaths.ModsPath, mod.Name);
            }

            return null;
        }

        public void Init()
        {
            init = true;
            isThisHostDedicated = (MyAPIGateway.Multiplayer.IsServer && MyAPIGateway.Utilities.IsDedicated);

            Log.Init();

            if(!isThisHostDedicated)
            {
                pathToMod = GetModPath(WORKSHOP_ID, WORKSHOP_ID_DEV, LOCAL_FOLDER, LOCAL_FOLDER_DEV);

                if(pathToMod == null)
                    Log.Error("Can't find this mod in the mod list!");

                settings = new Settings();

                MyAPIGateway.Utilities.MessageEntered += MessageEntered;
                MyAPIGateway.Multiplayer.RegisterMessageHandler(PACKET, ReceivedPacket);
            }
        }

        protected override void UnloadData()
        {
            try
            {
                if(init)
                {
                    init = false;

                    if(!isThisHostDedicated)
                    {
                        if(settings != null)
                        {
                            settings.Close();
                            settings = null;
                        }

                        MyAPIGateway.Utilities.MessageEntered -= MessageEntered;
                        MyAPIGateway.Multiplayer.UnregisterMessageHandler(PACKET, ReceivedPacket);
                    }
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }

            Log.Close();

            players.Clear();
            ents.Clear();
            detectableEnts.Clear();
            hits.Clear();

            interactEntity.Clear();
            removeInteractEntity.Clear();

            prevKeys.Clear();
            keys.Clear();

            planets.Clear();
            gravityGenerators.Clear();
        }

        public void MessageEntered(string msg, ref bool send)
        {
            try
            {
                if(msg.StartsWith("/interaction", StringComparison.InvariantCultureIgnoreCase))
                {
                    send = false;
                    msg = msg.Substring("/interaction".Length).Trim().ToLower();

                    if(msg.Equals("reload"))
                    {
                        if(settings.Load())
                            MyAPIGateway.Utilities.ShowMessage(Log.modName, "Reloaded and re-saved config.");
                        else
                            MyAPIGateway.Utilities.ShowMessage(Log.modName, "Config created with the current settings.");

                        // reset cables
                        foreach(var i in interactEntity.Values)
                        {
                            i.Clear();
                        }

                        settings.Save();
                        return;
                    }

                    MyAPIGateway.Utilities.ShowMessage(Log.modName, "Available commands:");
                    MyAPIGateway.Utilities.ShowMessage("/interaction reload ", "reloads the config file.");
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        public void ReceivedPacket(byte[] bytes)
        {
            try
            {
                int index = 0;

                var type = (InteractionType)bytes[index];
                index += sizeof(byte);

                long entId = BitConverter.ToInt64(bytes, index);
                index += sizeof(long);

                if(!MyAPIGateway.Entities.EntityExists(entId))
                    return;

                var ent = MyAPIGateway.Entities.GetEntityById(entId);
                var skinned = ent as MySkinnedEntity;

                if(skinned == null)
                    return;

                if(bytes.Length > index)
                {
                    long targetId = BitConverter.ToInt64(bytes, index);
                    index += sizeof(long);

                    if(bytes.Length > index)
                    {
                        var pos = new Vector3(BitConverter.ToSingle(bytes, index),
                                              BitConverter.ToSingle(bytes, index + sizeof(float)),
                                              BitConverter.ToSingle(bytes, index + sizeof(float) * 2));
                        index += sizeof(float) * 3;

                        InternalInteraction(ent, type, targetId, pos);
                    }
                    else
                        InternalInteraction(ent, type, targetId);
                }
                else
                {
                    InternalInteraction(ent, type);
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        public void TriggerInteraction(IMyCharacter character, InteractionType type, IMyEntity target = null, Vector3? offset = null, bool forceStayOnLastFrame = false)
        {
            try
            {
                var effect = interactionEffect[type];

                effect.PlayAnimation(character, forceStayOnLastFrame);

                if(target != null)
                    InternalInteraction(character, type, target.EntityId, offset);
                else
                    InternalInteraction(character, type);

                if(effect.sound != null)
                {
                    int len = sizeof(byte) + sizeof(long);

                    if(target != null)
                    {
                        len += sizeof(long);

                        if(offset.HasValue)
                            len += sizeof(float) * 3;
                    }

                    var bytes = new byte[len];
                    bytes[0] = (byte)type; // interaction type
                    len = 1;

                    var data = BitConverter.GetBytes(character.EntityId);
                    Array.Copy(data, 0, bytes, len, data.Length);
                    len += data.Length;

                    if(target != null)
                    {
                        data = BitConverter.GetBytes(target.EntityId);
                        Array.Copy(data, 0, bytes, len, data.Length);
                        len += data.Length;

                        if(offset.HasValue)
                        {
                            data = BitConverter.GetBytes(offset.Value.X);
                            Array.Copy(data, 0, bytes, len, data.Length);
                            len += data.Length;

                            data = BitConverter.GetBytes(offset.Value.Y);
                            Array.Copy(data, 0, bytes, len, data.Length);
                            len += data.Length;

                            data = BitConverter.GetBytes(offset.Value.Z);
                            Array.Copy(data, 0, bytes, len, data.Length);
                            len += data.Length;
                        }
                    }

                    var pos = character.WorldMatrix.Translation;
                    bool checkDistance = true;

                    switch(type)
                    {
                        case InteractionType.START:
                        case InteractionType.START_TARGET:
                        case InteractionType.END:
                        case InteractionType.END_TARGET:
                            checkDistance = false;
                            break;
                    }

                    MyAPIGateway.Players.GetPlayers(players, delegate (IMyPlayer p)
                                                    {
                                                        if(p.SteamUserId != MyAPIGateway.Multiplayer.MyId)
                                                        {
                                                            if(checkDistance && Vector3D.DistanceSquared(p.GetPosition(), pos) > PACKET_SOUND_DISTANCE)
                                                                return false;

                                                            MyAPIGateway.Multiplayer.SendMessageTo(PACKET, bytes, p.SteamUserId);
                                                        }

                                                        return false;
                                                    });
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        private void InternalInteraction(IMyEntity ent, InteractionType type, long targetId = 0, Vector3? offset = null)
        {
            string sound = interactionEffect[type].sound;

            if(type == InteractionType.START_TARGET)
            {
                if(targetId == 0)
                    return;

                if(!interactEntity.ContainsKey(ent.EntityId))
                {
                    interactEntity.Add(ent.EntityId, new Interacted()
                    {
                        target = targetId,
                        offset = offset,
                        time = DateTime.UtcNow.Ticks,
                    });
                }
                else
                {
                    var interact = interactEntity[ent.EntityId];
                    interact.remove = false;
                    interact.target = targetId;
                    interact.offset = offset;
                    interact.time = DateTime.UtcNow.Ticks;
                    interact.cableAtFingers = false;
                    interact.Clear();
                }

                PlaySound(ent, sound);
                return;
            }
            else if(type == InteractionType.END_TARGET)
            {
                if(interactEntity.ContainsKey(ent.EntityId))
                {
                    interactEntity[ent.EntityId].remove = true;
                    interactEntity[ent.EntityId].time = DateTime.UtcNow.Ticks;
                    PlaySound(ent, sound);
                    return;
                }
            }

            if(characterEntity != null && characterEntity == ent)
                return; // block click/tab/type sounds from your own menus

            PlaySound(ent, sound);
        }

        private bool InteractLoop(long charId, Interacted interacted)
        {
            if(!MyAPIGateway.Entities.EntityExists(charId) || !MyAPIGateway.Entities.EntityExists(interacted.target))
                return false;

            var charEnt = MyAPIGateway.Entities.GetEntityById(charId) as MySkinnedEntity;

            if(charEnt == null)
                return false;

            if(Vector3D.DistanceSquared(MyAPIGateway.Session.Camera.WorldMatrix.Translation, charEnt.WorldMatrix.Translation) > CABLE_DRAWDISTANCE_SQ)
            {
                interacted.Clear();
                return true;
            }

            long time = DateTime.UtcNow.Ticks;
            bool targetFinger = false;

            if(interacted.remove)
            {
                if(interacted.time == 0)
                {
                    return false;
                }
                else if(interacted.time + DELAY_DISCONNECT >= time)
                {
                    // wait...
                }
                else if(interacted.time + DELAY_PUTCABLE >= time)
                {
                    targetFinger = true;
                }
                else
                {
                    interacted.time = 0;
                    return false;
                }
            }
            else if(interacted.time != 0)
            {
                if((interacted.time + DELAY_GRABCABLE) >= time)
                {
                    return true;
                }
                else if((interacted.time + DELAY_CONNECT) >= time)
                {
                    targetFinger = true;
                }
                else
                {
                    interacted.time = 0;
                }
            }

            var targetEnt = MyAPIGateway.Entities.GetEntityById(interacted.target);
            var targetPos = targetEnt.WorldMatrix.Translation;

            if(targetFinger)
            {
                int fingerBone;

                if(charEnt.AnimationController.FindBone(BONE_RIGHTHAND_INDEXTIP, out fingerBone) == null)
                    return false;

                var fingerBoneMatrix = charEnt.BoneAbsoluteTransforms[fingerBone];
                targetPos = charEnt.WorldMatrix.Translation + Vector3.TransformNormal(fingerBoneMatrix.Translation, charEnt.WorldMatrix);
            }
            else
            {
                if(interacted.offset.HasValue)
                    targetPos += Vector3.TransformNormal(interacted.offset.Value, targetEnt.WorldMatrix);

                if(targetEnt is MyCubeBlock)
                    targetPos -= Vector3.TransformNormal((targetEnt as MyCubeBlock).BlockDefinition.ModelOffset, targetEnt.WorldMatrix);
            }

            int lcdBone;

            if(charEnt.AnimationController.FindBone(BONE_LEFTARM_LCD, out lcdBone) == null)
                return false;

            var lcdBoneMatrix = charEnt.BoneAbsoluteTransforms[lcdBone];
            var charPos = charEnt.WorldMatrix.Translation + Vector3.TransformNormal(lcdBoneMatrix.Translation, charEnt.WorldMatrix); // + (charEnt.WorldMatrix.Up * 0.05)
            var distSq = Vector3D.DistanceSquared(charPos, targetPos);

            if(distSq > 225) // 15m squared
            {
                interacted.Clear();
                return true;
            }

            if(++interacted.skipGravity >= SKIP_TICKS_GRAVITY)
            {
                interacted.skipGravity = 0;

                var ctrlEnt = (charEnt as VRage.Game.ModAPI.Interfaces.IMyControllableEntity);

                interacted.gravity = (ctrlEnt.EnabledThrusts ? MyParticlesManager.CalculateGravityInPoint(charPos) : charEnt.Physics.Gravity);
                interacted.inGravity = (interacted.gravity.LengthSquared() > 0);
            }

            const int segments = 5;
            double maxLenSq = 0.008;
            maxLenSq *= maxLenSq;

            if(interacted.cable == null)
            {
                interacted.cableAtFingers = targetFinger;
                interacted.cable = new List<CableJoint>();

                for(int i = 0; i < segments; i++)
                {
                    interacted.cable.Add(new CableJoint() { position = Vector3D.Lerp(charPos, targetPos, ((double)i / (double)segments)) });
                }
            }

            int lastIndex = interacted.cable.Count - 1;

            interacted.cable[0].position = charPos;
            interacted.cable[lastIndex].position = targetPos;

            if(interacted.cableAtFingers != targetFinger)
            {
                interacted.cableAtFingers = targetFinger;

                for(int i = 1; i < lastIndex; i++) // recalculate the cable segment positions to avoid it jumping around when changing anchor positions
                {
                    interacted.cable[i].position = Vector3D.Lerp(charPos, targetPos, ((double)i / (double)segments));
                }
            }

            // fake cable physics - please do not take without permission
            for(int i = 1; i < lastIndex; i++)
            {
                var r = interacted.cable[i];
                var p = interacted.cable[i - 1];
                var n = interacted.cable[i + 1];

                if(interacted.inGravity)
                    r.velocity += interacted.gravity / 9.81f;

                Vector3D pDir = (p.position - r.position);
                Vector3D nDir = (n.position - r.position);
                double pDirLen = pDir.LengthSquared();
                double nDirLen = nDir.LengthSquared();

                var dir = pDir * 30 * Math.Min(pDirLen, 1);
                dir += nDir * 30 * Math.Min(nDirLen, 1);
                r.velocity += (dir + (dir - r.prevDir) * 5);
                r.prevDir = dir;
                r.position += (r.velocity / 60.0f) / 10.0f;
            }

            Vector3D p0, p1, p2, p3;
            MyEntity e;

            for(int i = 0; i < lastIndex; i++)
            {
                var r = interacted.cable[i];
                var n = interacted.cable[i + 1];

                p0 = r.position;
                p1 = Vector3D.Lerp(r.position, n.position, 1.0 / 3.0);
                p2 = Vector3D.Lerp(r.position, n.position, 2.0 / 3.0);
                p3 = n.position;

                if(i > 0)
                {
                    var p = interacted.cable[i - 1];
                    var p_p1 = Vector3D.Lerp(r.position, p.position, 1.0 / 3.0);
                    var center = (p_p1 + p1) / 2;
                    p1 = r.position + (p1 - center);
                }

                if(i < (lastIndex - 1))
                {
                    var n2 = interacted.cable[i + 2];
                    var n2_p1 = Vector3D.Lerp(n.position, n2.position, 1.0 / 3.0);
                    var center = (p2 + n2_p1) / 2;
                    p2 = n.position + (p2 - center);
                }

                double step = (1.0 / ((double)settings.cableResolution / (double)segments));
                bool model = settings.cableModel > 0;
                int modelIndex = 0;

                if(model && r.ents == null)
                {
                    r.ents = new List<MyEntity>((int)(1 / step));
                }

                for(double t = step; Math.Round(t, 3) <= 1.0; t += step)
                {
                    var start = GetCurvePointAt(ref p0, ref p1, ref p2, ref p3, t - (step + 0.005));
                    var end = GetCurvePointAt(ref p0, ref p1, ref p2, ref p3, t);

                    if(model && pathToCable != null)
                    {
                        if(r.ents.Count > modelIndex)
                        {
                            e = r.ents[modelIndex];
                        }
                        else
                        {
                            e = new MyEntity();
                            e.Save = false;
                            e.SyncFlag = false;
                            e.IsPreview = true;
                            e.Init(null, pathToCable, null, null, null);
                            e.Flags = EntityFlags.Visible | EntityFlags.NeedsDraw | EntityFlags.NeedsDrawFromParent | EntityFlags.InvalidateOnMove;
                            MyEntities.Add(e, true);
                            r.ents.Add(e);
                        }

                        Vector3 dir = (start - end);
                        double width = settings.cableWidth * 10;
                        var scale = new Vector3D(width, width, dir.Length() * 10);
                        var matrix = MatrixD.CreateWorld((start + end) / 2, dir, Vector3.Up);
                        MatrixD.Rescale(ref matrix, ref scale);
                        e.WorldMatrix = matrix;
                        modelIndex++;
                    }
                    else
                    {
                        Vector3 dir = (end - start);
                        float len = dir.Normalize();

                        if(len > 0.001f)
                        {
                            float width = (float)settings.cableWidth / 2;
                            MyTransparentGeometry.AddLineBillboard(MATERIAL_WEAPONLASER, CABLE_COLOR, start, dir, len, width);
                        }
                    }
                }
            }

            return true;
        }

        public override void UpdateAfterSimulation()
        {
            try
            {
                if(!init)
                {
                    if(MyAPIGateway.Session == null)
                        return;

                    Init();
                }

                if(isThisHostDedicated)
                    return;

                if(++skipPlanets >= SKIP_TICKS_PLANETS)
                {
                    skipPlanets = 0;
                    planets.Clear();
                    MyAPIGateway.Entities.GetEntities(ents, delegate (IMyEntity e)
                                                      {
                                                          var planet = e as MyPlanet;

                                                          if(planet != null)
                                                              planets.Add(planet);

                                                          return false; // no reason to add to the list
                                                      });
                }

                if(settings.cables && interactEntity.Count > 0)
                {
                    foreach(var kv in interactEntity)
                    {
                        if(!InteractLoop(kv.Key, kv.Value))
                        {
                            kv.Value.Clear();
                            removeInteractEntity.Add(kv.Key);
                        }
                    }

                    if(removeInteractEntity.Count > 0)
                    {
                        foreach(var key in removeInteractEntity)
                        {
                            interactEntity.Remove(key);
                        }

                        removeInteractEntity.Clear();
                    }
                }

                var newCharacterEntity = MyAPIGateway.Session?.Player?.Character;

                if(newCharacterEntity == null)
                    return;

                if(characterEntity != newCharacterEntity)
                {
                    unsupportedCharacter = false;
                    characterEntity = newCharacterEntity;
                    var charSkinned = (MySkinnedEntity)characterEntity;
                    var subtypeId = ((MyCharacterDefinition)characterEntity.Definition).Id.SubtypeName;
                    int bone;

                    if(subtypeId != "Default_Astronaut")
                    {
                        unsupportedCharacter = true;
                        Log.Info("WARNING: Custom character models can't be supported by animated interaction mod!");
                    }
                    else if(charSkinned.AnimationController.FindBone(BONE_LEFTARM_LCD, out bone) == null || charSkinned.AnimationController.FindBone(BONE_RIGHTHAND_INDEXTIP, out bone) == null)
                    {
                        unsupportedCharacter = true;
                        Log.Info("WARNING: Default character model was changed and has different bones, due to ModAPI limitations the animated interaction mod can't find what those bones are renamed to and the mod simply won't work on this character model.");
                    }
                }

                if(unsupportedCharacter)
                    return;

                var inMenu = MyAPIGateway.Gui.IsCursorVisible;
                var controlled = MyAPIGateway.Session.ControlledObject;
                var interactedEntity = (controlled is IMyCharacter ? MyAPIGateway.Gui.InteractedEntity as IMyCubeBlock : null);

                #region button press/holding animations
                bool selected = false;

                if(!inMenu && !(controlled is MyRemoteControl))
                {
                    var detectorComp = characterEntity.Components.Get<MyCharacterDetectorComponent>();
                    var useObject = detectorComp.UseObject;

                    if(useObject != null && useObject.SupportedActions.HasFlag(UseActionEnum.Manipulate))
                    {
                        selected = true;

                        if(useObject.ContinuousUsage)
                        {
                            if(MyAPIGateway.Input.IsGameControlPressed(MyControlsSpace.USE))
                            {
                                if(!lastHoldingUse)
                                {
                                    lastHoldingUse = true;
                                    TriggerInteraction(characterEntity, InteractionType.USE_HOLD_ON);
                                }
                            }
                            else if(lastHoldingUse)
                            {
                                lastHoldingUse = false;
                                TriggerInteraction(characterEntity, InteractionType.USE_HOLD_OFF);
                            }
                        }
                        else
                        {
                            // HACK disabled for now
                            //long time = DateTime.UtcNow.Ticks;
                            //
                            //if(lastInteraction == 0 || lastInteraction <= time)
                            //{
                            //    if(MyAPIGateway.Input.IsNewGameControlPressed(MyControlsSpace.USE))
                            //    {
                            //        lastInteraction = time + interactionEffect[InteractionType.USE_BUTTON].delayTicks;
                            //
                            //        TriggerInteraction(characterEntity, InteractionType.USE_BUTTON);
                            //    }
                            //}
                        }
                    }
                }

                if(lastHoldingUse && !selected)
                {
                    lastHoldingUse = false;
                    TriggerInteraction(characterEntity, InteractionType.USE_HOLD_OFF);
                }
                #endregion button press/holding animations

                if(inMenu || controlled is MyRemoteControl || MyAPIGateway.Session.CameraController is MyCameraBlock)
                {
                    var page = MyAPIGateway.Gui.GetCurrentScreen;

                    if(!lcdShown)
                    {
                        lcdShown = true;
                        lastPage = page;

                        if(interactedEntity != null)
                        {
                            Vector3? offset = null;
                            var block = interactedEntity as MyCubeBlock;
                            var useObject = characterEntity.Components.Get<MyCharacterDetectorComponent>()?.UseObject;

                            if(block != null && useObject != null)
                            {
                                var blockCenter = block.WorldMatrix.Translation - Vector3D.TransformNormal(block.BlockDefinition.ModelOffset, block.WorldMatrix);
                                offset = Vector3D.TransformNormal(useObject.ActivationMatrix.Translation - blockCenter, block.PositionComp.WorldMatrixInvScaled);
                            }

                            TriggerInteraction(characterEntity, InteractionType.START_TARGET, interactedEntity, offset);
                            startTime = DateTime.UtcNow.Ticks + interactionEffect[InteractionType.START_TARGET].delayTicks;
                        }
                        else
                        {
                            TriggerInteraction(characterEntity, InteractionType.START);
                            startTime = DateTime.UtcNow.Ticks + interactionEffect[InteractionType.START].delayTicks;
                        }
                    }
                    else
                    {
                        long time = DateTime.UtcNow.Ticks;

                        if(startTime == 0 || startTime <= time)
                        {
                            startTime = 0;

                            if(inMenu)
                            {
                                if(lastInteraction == 0 || lastInteraction <= time)
                                {
                                    InteractionType type = InteractionType.START;

                                    if(lastInRC)
                                    {
                                        lastInRC = false;
                                        type = InteractionType.RC_OFF;
                                    }
                                    else if(lastInCamera)
                                    {
                                        lastInCamera = false;
                                        type = InteractionType.CAMERA_OFF;
                                    }
                                    else if(lastPage != page)
                                    {
                                        lastPage = page;
                                        type = InteractionType.TAB;
                                    }
                                    else if(MyAPIGateway.Input.IsAnyNewMouseOrJoystickPressed())
                                    {
                                        type = InteractionType.CLICK;
                                    }
                                    else if(MyAPIGateway.Input.IsAnyKeyPress())
                                    {
                                        keys.Clear();
                                        MyAPIGateway.Input.GetPressedKeys(keys);

                                        if(!KeyListEqual(keys, prevKeys))
                                        {
                                            prevKeys = new HashSet<MyKeys>(keys);
                                            var use = MyAPIGateway.Input.GetGameControl(MyControlsSpace.USE);
                                            var terminal = MyAPIGateway.Input.GetGameControl(MyControlsSpace.TERMINAL);

                                            foreach(var k in keys)
                                            {
                                                if(k == use.GetKeyboardControl() || k == use.GetSecondKeyboardControl() || k == terminal.GetKeyboardControl() || k == terminal.GetSecondKeyboardControl())
                                                    continue;

                                                if(MyAPIGateway.Input.IsKeyValid(k))
                                                {
                                                    type = InteractionType.TYPE;
                                                    break;
                                                }
                                            }
                                        }

                                        keys.Clear();
                                    }

                                    if(type != InteractionType.START)
                                    {
                                        lastInteraction = time + interactionEffect[type].delayTicks;

                                        TriggerInteraction(characterEntity, type, forceStayOnLastFrame: true);
                                    }
                                }
                                else
                                {
                                    lastPage = page;
                                }
                            }
                            else if(controlled is MyRemoteControl)
                            {
                                if(!lastInRC)
                                {
                                    lastInteraction = time + interactionEffect[InteractionType.RC_ON].delayTicks;
                                    lastInRC = true;

                                    TriggerInteraction(characterEntity, InteractionType.RC_ON);
                                }
                            }
                            else if(MyAPIGateway.Session.CameraController is MyCameraBlock)
                            {
                                if(!lastInCamera)
                                {
                                    lastInteraction = time + interactionEffect[InteractionType.RC_ON].delayTicks;
                                    lastInCamera = true;

                                    TriggerInteraction(characterEntity, InteractionType.CAMERA_ON);
                                }
                            }
                        }
                    }
                }
                else if(lcdShown)
                {
                    lcdShown = false;
                    lastPage = MyTerminalPageEnum.None;
                    lastInteraction = 0;
                    lastInCamera = false;
                    lastInRC = false;

                    InteractionType type = InteractionType.END;

                    if(interactEntity.ContainsKey(characterEntity.EntityId))
                        type = InteractionType.END_TARGET;

                    TriggerInteraction(characterEntity, type);
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        private static bool KeyListEqual(List<MyKeys> keys, HashSet<MyKeys> prevKeys)
        {
            if(keys.Count != prevKeys.Count)
                return false;

            foreach(var k in keys)
            {
                if(!prevKeys.Contains(k))
                    return false;
            }

            return true;
        }

        public void PlaySound(IMyEntity ent, string name)
        {
            if(name == null || settings.interactionSounds <= 0)
                return;

            var emitter = new MyEntity3DSoundEmitter(ent as MyEntity);
            emitter.CustomVolume = settings.interactionSounds;
            emitter.PlaySingleSound(new MySoundPair(name));
        }

        public static Vector3D GetCurvePointAt(ref Vector3D p0, ref Vector3D p1, ref Vector3D p2, ref Vector3D p3, double t)
        {
            return new Vector3D(
                cubeBezier(p0.X, p1.X, p2.X, p3.X, t),
                cubeBezier(p0.Y, p1.Y, p2.Y, p3.Y, t),
                cubeBezier(p0.Z, p1.Z, p2.Z, p3.Z, t));
        }

        private static double cubeBezier(double p0, double p1, double p2, double p3, double t)
        {
            double rt = 1.0 - t;
            double rtt = rt * t;
            return rt * rt * rt * p0 + 3 * rt * rtt * p1 + 3 * rtt * t * p2 + t * t * t * p3;
        }
    }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_GravityGenerator), true)]
    public class GravityGeneratorFlat : GravityGeneratorLogic { }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_GravityGeneratorSphere), true)]
    public class GravityGeneratorSphere : GravityGeneratorLogic { }

    public class GravityGeneratorLogic : MyGameLogicComponent
    {
        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            Entity.NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            try
            {
                var block = Entity as IMyGravityGeneratorBase;

                if(block.CubeGrid.Physics == null)
                    return;

                InteractionMod.instance.gravityGenerators.Add(block.EntityId, block);
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        public override void Close()
        {
            try
            {
                InteractionMod.instance.gravityGenerators.Remove(Entity.EntityId);
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }
    }
}