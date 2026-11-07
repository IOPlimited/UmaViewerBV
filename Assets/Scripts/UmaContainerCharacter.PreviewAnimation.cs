using UnityEngine;

public partial class UmaContainerCharacter
{
    public AnimationClip PreviewLegacyClip { get; private set; }
    public float PreviewLegacyTime { get; private set; }
    public float PreviewLegacySpeed { get; private set; } = 1f;
    AnimationClip previewLegacyFace, previewLegacyEar;
    bool PreviewLegacyLoops => PreviewLegacyClip != null &&
        (PreviewLegacyClip.isLooping || PreviewLegacyClip.name.Contains("_loop"));

    void PlayPreviewLegacy(AnimationClip clip)
    {
        StopPreviewLegacy();
        UpBodyReset();
        PreviewLegacyClip = clip;
        PreviewLegacyTime = 0;
        PreviewLegacySpeed = UI.AnimationSettings.SpeedSlider.value;
        UmaAnimator.enabled = false;
        Builder.SetPreviewCamera(null);
        if (FaceDrivenKeyTarget != null)
        {
            FaceDrivenKeyTarget.ResetLocator();
            string facial = clip.name.Replace("/body/", "/facial/");
            if (Main.AbList.TryGetValue(facial + "_face", out var face))
                previewLegacyFace = face.Get<AnimationClip>();
            if (Main.AbList.TryGetValue(facial + "_ear", out var ear))
                previewLegacyEar = ear.Get<AnimationClip>();
            if (previewLegacyFace != null || previewLegacyEar != null)
                UmaFaceAnimator.enabled = false;
        }
        SamplePreviewLegacy();
    }

    void StopPreviewLegacy()
    {
        if (PreviewLegacyClip == null) return;
        PreviewLegacyClip = null;
        previewLegacyFace = previewLegacyEar = null;
        UmaAnimator.enabled = true;
        if (UmaFaceAnimator != null) UmaFaceAnimator.enabled = true;
    }

    void AdvancePreviewLegacy(float delta)
    {
        if (IsLive || PreviewLegacyClip == null || UI.PoseManager.PoseModeOn) return;
        float next = PreviewLegacyTime + delta * PreviewLegacySpeed;
        PreviewLegacyTime = PreviewLegacyLoops && PreviewLegacyClip.length > 0
            ? Mathf.Repeat(next, PreviewLegacyClip.length)
            : Mathf.Clamp(next, 0, PreviewLegacyClip.length);
        SamplePreviewLegacy();
    }

    void SamplePreviewLegacy()
    {
        PreviewLegacyClip.SampleAnimation(gameObject, PreviewLegacyTime);
        if (previewLegacyFace != null)
            previewLegacyFace.SampleAnimation(UmaFaceAnimator.gameObject, PreviewLegacyTime);
        if (previewLegacyEar != null)
            previewLegacyEar.SampleAnimation(UmaFaceAnimator.gameObject, PreviewLegacyTime);
        if (previewLegacyFace != null || previewLegacyEar != null)
            FaceDrivenKeyTarget.ProcessLocator();
    }

    public void SetPreviewLegacySpeed(float speed) => PreviewLegacySpeed = speed;

    public void SeekPreviewLegacy(float normalizedTime)
    {
        PreviewLegacyTime = Mathf.Clamp01(normalizedTime) * PreviewLegacyClip.length;
        PreviewLegacySpeed = 0;
        SamplePreviewLegacy();
    }

    public void TogglePreviewLegacy(float speed)
    {
        if (PreviewLegacySpeed > 0) PreviewLegacySpeed = 0;
        else
        {
            if (PreviewLegacyTime >= PreviewLegacyClip.length) PreviewLegacyTime = 0;
            PreviewLegacySpeed = speed;
        }
    }
}
