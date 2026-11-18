using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Gallop;
using Gallop.Live;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

// Owns the motion overlays for the default character preview.
internal sealed class PreviewIdleController : IDisposable
{
    readonly UmaContainerCharacter actor;
    readonly UmaDatabaseEntry idleMotion;
    readonly FaceDrivenKeyTarget face;
    readonly List<UmaDatabaseEntry> assets = new List<UmaDatabaseEntry>();
    readonly LiveEyeBlinkClock blink = new LiveEyeBlinkClock { AutoBlink = true, RandomRange = Random.Range };
    EyeBlinkSettingData blinkSetting;
    FaceEyeType blinkPose;

    readonly List<DrivenKeyAnimation> ears = new List<DrivenKeyAnimation>();
    GameObject earSampler;
    LiveEarAnimationPlayer earPlayer;
    LiveEarWeightTransition earWeights;
    int earIndex = -1;
    float earWait;

    readonly List<AnimationClip> tails = new List<AnimationClip>();
    readonly List<(Transform target, Transform sample)> tailBones = new List<(Transform, Transform)>();
    readonly List<(CySpringBoneBase bone, float ratio)> tailSprings = new List<(CySpringBoneBase, float)>();
    GameObject tailSampler;
    int tailIndex = -1;
    float tailWait, tailTime;
    bool tailPlaying;

    bool selected, ready, disposed;

    public PreviewIdleController(UmaContainerCharacter actor, UmaDatabaseEntry idleMotion)
    {
        this.actor = actor;
        this.idleMotion = idleMotion;
        face = actor.FaceDrivenKeyTarget;
        blink.FaceStep += (step, _) =>
        {
            if (step == 0 || step == 3) blinkPose = FaceEyeType.HalfA;
            else if (step == 1) blinkPose = FaceEyeType.CloseA;
            else if (step == 4) blinkPose = FaceEyeType.Base;
        };
    }

    public IEnumerator Load()
    {
        var database = UmaDatabaseController.Instance;
        var earEntries = ResolveMotions(() => database.ReadLiveRandomEarMotionNames(EarType.Base),
            "3d/motion/event/facial/type00/anm_eve_type00_", "_driven");
        var tailEntries = ResolveMotions(database.ReadRandomTailMotionNames,
            "3d/motion/event/body/type00/tail/anm_eve_type00_", "");
        UmaViewerMain.Instance.AbList.TryGetValue("3d/animator/ast_eyeblink_set", out var blinkEntry);
        if (Config.Instance.DownloadMissingResources || Config.Instance.WorkMode == WorkMode.Standalone)
        {
            var entries = earEntries.Concat(tailEntries).Append(blinkEntry).Where(entry => entry != null);
            var dependencies = entries.SelectMany(entry => UmaAssetManager.SearchAB(UmaViewerMain.Instance, entry))
                .Distinct().ToList();
            if (dependencies.Count != 0)
                yield return UmaViewerDownload.DownloadAssets(dependencies, UmaSceneController.instance.LoadingProgressChange);
        }

        // MergeModel destroys the placeholder bones at the end of the load frame.
        yield return null;
        if (disposed) yield break;

        blinkSetting = LoadAsset<EyeBlinkSettingData>(blinkEntry);
        foreach (var entry in earEntries)
        {
            var animation = LoadAsset<DrivenKeyAnimation>(entry);
            if (animation != null && animation.clip != null && animation.clip.length > 0f &&
                animation.animationEarType != null && animation.animationEarType.Length <= 2 &&
                animation.animationEarType.All(side => side != null && side.faceType != null &&
                    side.count >= 0 && side.count <= side.faceType.Length && side.faceType.All(type => (uint)type <= 16)))
                ears.Add(animation);
        }

        // LoadFaceMorph already owns this locator bundle for the actor.
        var locator = UmaAssetManager.Get("3d/animator/drivenkeylocator")?.LoadAsset<GameObject>("DrivenKeyLocator");
        if (face != null && ears.Count > 0 && locator != null)
        {
            earSampler = Object.Instantiate(locator);
            earSampler.hideFlags = HideFlags.HideAndDontSave;
            earPlayer = new LiveEarAnimationPlayer(earSampler);
            earWeights = new LiveEarWeightTransition();
        }

        var tailRoot = actor.transform.Find("Position/Hip/Tail_Ctrl");
        if (tailRoot != null)
        {
            foreach (var entry in tailEntries)
            {
                var clip = LoadAsset<AnimationClip>(entry);
                if (clip != null && clip.length > 0f) tails.Add(clip);
            }
            if (tails.Count > 0) BindTail(tailRoot);
        }

        ResetBlink();
        earWait = RandomInterval("ear", 0.5f);
        tailWait = RandomInterval("tail", 0.8f);
        ready = true;
    }

    static List<UmaDatabaseEntry> ResolveMotions(Func<string[]> readNames, string prefix, string suffix)
    {
        var entries = new List<UmaDatabaseEntry>();
        try
        {
            foreach (string motion in readNames())
                if (UmaViewerMain.Instance.AbList.TryGetValue((prefix + motion + suffix).ToLowerInvariant(), out var entry))
                    entries.Add(entry);
        }
        catch (Mono.Data.Sqlite.SqliteException exception)
        {
            Debug.LogWarning("Preview idle motions unavailable: " + exception.Message);
        }
        return entries;
    }

    T LoadAsset<T>(UmaDatabaseEntry entry) where T : Object
    {
        if (entry == null) return null;
        assets.Add(entry);
        return UmaAssetManager.LoadAssetBundle(entry)?.LoadAllAssets<T>().FirstOrDefault();
    }

    void BindTail(Transform root)
    {
        // Sample only the tail hierarchy, away from the model and its exporters.
        tailSampler = new GameObject("Preview idle tail sampler") { hideFlags = HideFlags.HideAndDontSave };
        Transform position = new GameObject(root.parent.parent.name).transform;
        position.SetParent(tailSampler.transform, false);
        Transform hip = new GameObject(root.parent.name).transform;
        hip.SetParent(position, false);
        BindTailBone(root, hip);

        var springs = new List<CySpringBoneBase>();
        actor.LiveCySpringController?.FindSpring(springs,
            name => name == "Sp_Hi_Tail0_B_00" || name == "Sp_Hi_Tail0_B_01",
            CySpringController.Parts.Tail, false);
        foreach (var spring in springs) tailSprings.Add((spring, spring.DynamicsRatio));
    }

    void BindTailBone(Transform bone, Transform parent)
    {
        Transform sample = new GameObject(bone.name).transform;
        sample.SetParent(parent, false);
        tailBones.Add((bone, sample));
        foreach (Transform child in bone) BindTailBone(child, sample);
    }

    float RandomInterval(string part, float defaultMin)
    {
        float Seconds(string suffix, float fallback)
        {
            string column = part + "_random_time_" + suffix;
            var row = actor.CharaData;
            return row != null && row.Table.Columns.Contains(column) && !row.IsNull(column)
                ? Convert.ToSingle(row[column]) / 100f : fallback;
        }
        return Random.Range(Seconds("min", defaultMin), Seconds("max", 1.5f));
    }

    static int PickMotion(int count, int previous)
    {
        if (count == 1) return 0;
        int index = Random.Range(0, count - (previous >= 0 ? 1 : 0));
        return previous >= 0 && index >= previous ? index + 1 : index;
    }

    public void SelectMotion(UmaDatabaseEntry motion)
    {
        Suspend();
        selected = motion == idleMotion;
    }

    public void Tick(float delta, bool allowFace)
    {
        if (!ready || !selected) return;
        if (delta <= 0f)
        {
            Suspend();
            return;
        }

        if (face != null)
        {
            if (allowFace)
            {
                UpdateBlink(delta);
                UpdateEars(delta);
            }
            else
            {
                ResetFace();
            }
        }
        if (tails.Count > 0) UpdateTail(delta);
    }

    void UpdateBlink(float delta)
    {
        FaceEyeType previous = blinkPose;
        bool suppressed = (actor.FaceOverrideData && actor.FaceOverrideData.Enable &&
            actor.FaceOverrideData.IsDisableEyeBlink) || face.EyeMorphs.Exists(morph =>
            morph.weight > 0.0001f && IgnoresBlink((FaceEyeType)morph.index));
        if (suppressed && blink.Step != 5) ResetBlink();
        else blink.Update(delta, suppressed);

        if (previous != FaceEyeType.Base || blinkPose != FaceEyeType.Base)
            face.ChangeMorphEye(blinkPose);
    }

    bool IgnoresBlink(FaceEyeType type)
    {
        switch (type)
        {
            case FaceEyeType.CloseA:
            case FaceEyeType.WaraiA:
            case FaceEyeType.WaraiD:
            case FaceEyeType.KusyoA:
            case FaceEyeType.EyeHideA:
            case FaceEyeType.EyelidHideA:
            case FaceEyeType.EyelidHideB:
                return true;
        }
        return blinkSetting?.IgnoreEyeBlinkType?.Contains(type) == true;
    }

    void ResetBlink()
    {
        blink.Step = 5;
        blink.DoubleStep = blink.SingleCount = 0;
        blink.TimeCount = 0f;
        blink.TimeMax = Random.Range(blink.IntervalMin, blink.IntervalMax);
        blinkPose = FaceEyeType.Base;
    }

    void UpdateEars(float delta)
    {
        if (earPlayer == null) return;
        if ((actor.FaceOverrideData && actor.FaceOverrideData.Enable) ||
            face.EarMorphs.Exists(morph => Mathf.Abs(morph.weight) > 0.0001f))
        {
            ResetEars();
            return;
        }

        if (!earPlayer.Playing && (earWait -= delta) <= 0f)
        {
            earIndex = PickMotion(ears.Count, earIndex);
            earPlayer.Play(ears[earIndex], earWeights, 0.2f);
            earWait = RandomInterval("ear", 0.5f);
        }
        if (earPlayer.Animation == null) return;
        earPlayer.Tick(delta, earWeights);
        earWeights.Advance(delta, DrivenKeyComponent.InterpolateType.Linear);
        face.ChangeMorphEar(earWeights);
    }

    void UpdateTail(float delta)
    {
        if (!tailPlaying)
        {
            tailWait -= delta;
            if (tailWait > 0f) return;
            tailIndex = PickMotion(tails.Count, tailIndex);
            tailTime = 0f;
            tailPlaying = true;
        }

        var clip = tails[tailIndex];
        tailTime = Mathf.Min(tailTime + delta, clip.length);
        float weight = Mathf.Clamp01(tailTime / 0.2f);
        float dynamics = Mathf.Max(1f - Mathf.Clamp01(tailTime / 0.5f),
            Mathf.Clamp01((tailTime - clip.length + 0.5f) / 0.5f));
        foreach (var (bone, ratio) in tailSprings) bone.DynamicsRatio = ratio * dynamics;

        foreach (var (target, sample) in tailBones)
        {
            sample.localPosition = target.localPosition;
            sample.localRotation = target.localRotation;
            sample.localScale = target.localScale;
        }

        clip.SampleAnimation(tailSampler, tailTime);
        foreach (var (target, sample) in tailBones)
        {
            target.localPosition = Vector3.Lerp(target.localPosition, sample.localPosition, weight);
            target.localRotation = Quaternion.Slerp(target.localRotation, sample.localRotation, weight);
            target.localScale = Vector3.Lerp(target.localScale, sample.localScale, weight);
        }
        if (tailTime >= clip.length) ResetTail();
    }

    void ResetEars()
    {
        if (earPlayer?.Animation == null) return;
        earPlayer.Clear();
        earWeights = new LiveEarWeightTransition();
        earWait = RandomInterval("ear", 0.5f);
        if (face != null) face.ChangeMorphEar();
    }

    void ResetFace()
    {
        if (blink.Step != 5)
        {
            bool restoreEyes = blinkPose != FaceEyeType.Base;
            ResetBlink();
            if (restoreEyes && face != null) face.ChangeMorphEye();
        }
        ResetEars();
    }

    void ResetTail()
    {
        if (!tailPlaying) return;
        foreach (var (bone, ratio) in tailSprings) bone.DynamicsRatio = ratio;
        tailWait = RandomInterval("tail", 0.8f);
        tailPlaying = false;
    }

    public void Suspend()
    {
        ResetFace();
        ResetTail();
    }

    public void Dispose()
    {
        if (disposed) return;
        Suspend();
        disposed = true;
        earPlayer?.Dispose();
        if (earSampler != null) Object.Destroy(earSampler);
        if (tailSampler != null) Object.Destroy(tailSampler);
        foreach (var entry in assets) UmaAssetManager.UnloadAssetBundle(entry, false);
    }
}
