using UnityEngine;

namespace Gallop.Live
{
    public partial class Director
    {
        readonly LiveModelPhaseRegistry liveModelPhases = new LiveModelPhaseRegistry();
        internal void ReserveFormationPropCySpring(int characterIndex, int dressIndex, float seconds) => _livePropsController?.ReserveWarmingUpCySpring(characterIndex, dressIndex, seconds);
        internal LiveModelPhaseRegistry RegisterLiveModelIK(UmaContainerCharacter character)
        {
            var phaseOwner = character.LiveModelPhaseOwner;
            if (phaseOwner == null)
            {
                phaseOwner = new LiveModelPhaseOwner(
                    () => character != null,
                    () => character.gameObject.activeSelf,
                    phase =>
                    {
                        var model = character.LiveModelIK;
                        switch (phase)
                        {
                            case LiveModelCallback.UpdatePre:
                                character.LiveMotionCanceler?.PreUpdate();
                                break;
                            case LiveModelCallback.PreLate:
                                character.LiveMicStand.PreLateUpdate();
                                break;
                            case LiveModelCallback.IK1:
                                model.Limb.Tick(LiveLimbIKRuntime.UpdatePhase.PreLateUpdateIK1);
                                if (model.Enabled) model.PreLateUpdateIK1();
                                break;
                            case LiveModelCallback.IK2:
                                if (model.Enabled) model.PreLateUpdateIK2();
                                break;
                            case LiveModelCallback.Late:
                                model.Limb.Tick(LiveLimbIKRuntime.UpdatePhase.LateUpdate);
                                character.LiveSystemTextLimbIK?.Tick(LiveLimbIKRuntime.UpdatePhase.LateUpdate);
                                if (!character.IsMini) character.BeginLiveModelLateUpdate();
                                break;
                            case LiveModelCallback.AfterLate:
                                character.LiveMotionCanceler?.AfterLateUpdate();
                                if (!character.IsMini) character.EndLiveModelLateUpdate();
                                break;
                        }
                        return 0;
                    });
                character.LiveModelPhaseOwner = phaseOwner;
            }

            liveModelPhases.AddItem(phaseOwner);
            return liveModelPhases;
        }
    }
}
