using Games.Rebuilt.Components;
using Games.Rebuilt.Robots;
using MoSimLib;
using RobotFramework.Components;
using RobotFramework.Controllers.PidSystems;
using RobotFramework.Enums;
using UnityEngine;

public class Clockwork : RebuiltTurretRobot
{
    [Header("Joints")]
    [SerializeField] private GenericTurret turret;
    [SerializeField] private GenericJoint hood;

    [Header("Rollers")]
    [SerializeField] private RollerGroup intakeRollers;
    [SerializeField] private RollerGroup spindexerWheel;
    [SerializeField] private RollerGroup spindexerCone;
    [SerializeField] private RollerGroup tunnelFeeder1;
    [SerializeField] private RollerGroup tunnelFeeder2;
    [SerializeField] private RollerGroup shooterFlywheel;

    [Header("PIDs")]
    [SerializeField] private PidConstants turretPid;
    [SerializeField] private PidConstants hoodPid;

    [Header("SOTM")]
    [SerializeField] private float velocityDampeningValue = 0.1f;

    [Header("Shooter Tuning")]
    [SerializeField] private RebuiltInterpolation azimuthFlywheelInterpolation;
    [SerializeField] private RebuiltInterpolation azimuthHoodInterpolation;
    [SerializeField] private RebuiltInterpolation azimuthTofInterpolation;
    [SerializeField] private RebuiltInterpolation neutralFlywheelInterpolation;
    [SerializeField] private RebuiltInterpolation neutralHoodInterpolation;
    [SerializeField] private RebuiltInterpolation neutralTofInterpolation;

    [Header("Thresholds")]
    [SerializeField] private float turretAngleTolerance = 3f;
    [SerializeField] private float turretAngleToleranceNeutral = 6f;
    [SerializeField] private float hoodAtTargetTolerance = 2f;
    [SerializeField] private float hoodAtTargetToleranceNeutral = 3f;

    [Header("Intake Tuning")]
    [SerializeField] private float intakeRpm = 2000f;
    [SerializeField] private float spindexerWheelRpm = 540f;
    [SerializeField] private float spindexerConeRpm = 270f;
    [SerializeField] private float tunnelFeeder1Rpm = 2970f;
    [SerializeField] private float tunnelFeeder2Rpm = 2970f;
    [SerializeField] private float uptakeRpm = 2970f;
    [SerializeField] private float manualShotRpm = 3000f;
    [SerializeField] private float intakeWobbleDelay = 1.25f;

    [Header("Drive Speed Multipliers")]
    [SerializeField, Range(0f, 1f)] private float shootingDriveMultiplier = 0.179f;
    [SerializeField, Range(0f, 1f)] private float shootingTurnMultiplier = 0.179f;
    [SerializeField, Range(0f, 1f)] private float passingDriveMultiplier = 0.263f;
    [SerializeField, Range(0f, 1f)] private float passingTurnMultiplier = 0.215f;

    [Header("Joint Axes")]
    [SerializeField] private JointAxis hoodJointAxis = JointAxis.Z;

    [Header("Turret Transform")]
    [SerializeField] private Transform turretTransform;

    private bool isShooting;
    private bool intakeDeployed;
    private bool intakeWobble;
    private float shootingTimer;
    private float hoodTarget;
    private float currentTof;
    private Vector3 turretVelocity;

    protected override void Init()
    {
        base.Init();

        turret.SetPid(turretPid);
        hood.SetPid(hoodPid);

        turret.UseNoWrap(true);
        turret.SetNoWrap(-150f, 510f);

        azimuthFlywheelInterpolation.Initialize(this);
        azimuthHoodInterpolation.Initialize(this);
        azimuthTofInterpolation.Initialize(this);
        neutralFlywheelInterpolation.Initialize(this);
        neutralHoodInterpolation.Initialize(this);
        neutralTofInterpolation.Initialize(this);

        intakeDeployed = false;
        intakeWobble = false;
        shootingTimer = 0f;
        hoodTarget = 0f;
        currentTof = 0.5f;
        turretVelocity = Vector3.zero;

        Scheduler.AddSubsystems(Intake, Hopper, Shooter, TurretTrack, UpdateTof);
        Scheduler.AddProtectedSystem(UpdateJoints, UpdateDriveSpeed, UpdateTurretVelocity);
        Scheduler.AddLateRecursive(UpdatePids);
    }

    private void UpdateTurretVelocity()
    {
        if (rb != null && turretTransform != null)
        {
            turretVelocity = rb.GetPointVelocity(turretTransform.position);
        }
    }

    private void UpdatePids()
    {
        turret.UpdatePid(turretPid);
        hood.UpdatePid(hoodPid);
    }

    private void UpdateJoints()
    {
        hood.SetTargetAngle(hoodTarget).withAxis(hoodJointAxis);
        turret.SetTrackContinuously(true);
    }

    private void TurretTrack()
    {
        bool inAllianceZone = InAllianceZone();
        bool isPassing = AutoPass || !inAllianceZone;

        if (isPassing)
        {
            turret.SetPassMapClosest();
        }
        else
        {
            turret.SetDefaultMap(RobotFramework.Components.GenericTurret.DefaultMap.Hub);
        }

        Vector3 targetPos = turret.GetCurrentTarget();
        Vector3 predictedTarget = targetPos - turretVelocity * currentTof;
        turret.SetCustomTarget(predictedTarget);
        turret.Align();
    }

    private void Shooter()
    {
        SetInterpMap();

        bool useNZ = !InAllianceZone() || AutoPass;
        float currentTurretTolerance = useNZ ? turretAngleToleranceNeutral : turretAngleTolerance;
        float currentHoodTolerance = useNZ ? hoodAtTargetToleranceNeutral : hoodAtTargetTolerance;

        var hoodInterp = useNZ ? neutralHoodInterpolation : azimuthHoodInterpolation;
        hoodInterp.SetCustomTarget(turret.GetCurrentTarget());
        hoodTarget = hoodInterp.Interpolate();

        var flywheelInterp = useNZ ? neutralFlywheelInterpolation : azimuthFlywheelInterpolation;
        flywheelInterp.SetCustomTarget(turret.GetCurrentTarget());
        float shooterSpeed = flywheelInterp.Interpolate();

        bool underTrench = UnderTrench(turretTransform, 0.17f);

        shooterFlywheel.SetRpm(underTrench ? -2000f : shooterSpeed);

        bool turretAligned = Mathf.Abs(turret.GetError()) < currentTurretTolerance;
        bool hoodAligned = Mathf.Abs(hood.GetAngle() - hoodTarget) < currentHoodTolerance;
        bool flywheelAtSpeed = shooterFlywheel.AtSpeed();
        bool readyToShoot = (useNZ || (turretAligned && hoodAligned && flywheelAtSpeed)) && !underTrench && turretAligned;

        if (readyToShoot || ManualShoot)
        {
            tunnelFeeder2.SetRpm(uptakeRpm);
            tunnelFeeder1.SetRpm(uptakeRpm);
            isShooting = true;
        }
        else
        {
            isShooting = false;
            tunnelFeeder2.OverrideRpm(-1000);
            tunnelFeeder1.OverrideRpm(-1000);
        }

        if (ManualShoot)
        {
            shooterFlywheel.SetRpm(manualShotRpm);
            hoodTarget = underTrench ? 0f : 17.8f;
        }

        if (turret.IsWrapping() || Mathf.Abs(turret.GetError()) > currentTurretTolerance)
        {
            tunnelFeeder2.FlipRpm();
            tunnelFeeder1.OverrideRpm(-1000);
        }

        intakeWobble = (shootingTimer >= intakeWobbleDelay);
    }

    private void SetInterpMap()
    {
        bool useNZ = !InAllianceZone() || AutoPass;

        if (useNZ)
        {
            neutralFlywheelInterpolation.SetPassMapClosest();
            neutralHoodInterpolation.SetPassMapClosest();
            neutralTofInterpolation.SetPassMapClosest();
        }
        else
        {
            azimuthFlywheelInterpolation.SetDefaultMap(Games.Rebuilt.Components.DefaultMap.Hub);
            azimuthHoodInterpolation.SetDefaultMap(Games.Rebuilt.Components.DefaultMap.Hub);
            azimuthTofInterpolation.SetDefaultMap(Games.Rebuilt.Components.DefaultMap.Hub);
        }
    }

    private void UpdateTof()
    {
        bool useNZ = !InAllianceZone() || AutoPass;
        var tofInterp = useNZ ? neutralTofInterpolation : azimuthTofInterpolation;

        if (useNZ)
        {
            tofInterp.SetPassMapClosest();
        }
        else
        {
            tofInterp.SetDefaultMap(Games.Rebuilt.Components.DefaultMap.Hub);
        }

        tofInterp.SetCustomTarget(turret.GetCurrentTarget());
        currentTof = tofInterp.Interpolate();
    }

    private void Intake()
    {
        if (intakeWobble)
        {
            Agitation();
        }
        else if (IntakeAction || intakeDeployed)
        {
            intakeDeployed = true;
            intakeRollers.SetRpm(intakeRpm);
            spindexerWheel.SetRpm(spindexerWheelRpm);
            spindexerCone.SetRpm(spindexerConeRpm);
            tunnelFeeder1.SetRpm(tunnelFeeder1Rpm);
            tunnelFeeder2.SetRpm(tunnelFeeder2Rpm);
        }

        if (IntakeAction)
        {
            intakeRollers.SetRpm(intakeRpm);
        }
        else if (intakeDeployed)
        {
            intakeRollers.SetRpm(intakeRpm * 0.2f);
        }
        else
        {
            intakeRollers.Stop();
            spindexerWheel.Stop();
            spindexerCone.Stop();
            tunnelFeeder1.Stop();
            tunnelFeeder2.Stop();
        }
    }

    private void Hopper()
    {
        intakeWobble = WantsToShoot() && !IntakeAction && (shootingTimer >= intakeWobbleDelay);
    }

    private void Agitation()
    {
        if (IntakeAction)
        {
            intakeRollers.SetRpm(intakeRpm);
            spindexerWheel.SetRpm(spindexerWheelRpm);
            spindexerCone.SetRpm(spindexerConeRpm);
            tunnelFeeder1.SetRpm(tunnelFeeder1Rpm);
            tunnelFeeder2.SetRpm(tunnelFeeder2Rpm);
            return;
        }
    }

    private bool WantsToShoot()
    {
        return OuttakeAction || AutoPass || ManualShoot;
    }

    protected override void Feed()
    {
        tunnelFeeder2.SetRpm(uptakeRpm);
        tunnelFeeder1.SetRpm(uptakeRpm);
    }

    protected override void IntakingAndShooting()
    {
        Intake();
        Shooter();
    }

    protected override void Idling()
    {
        intakeRollers.Stop();
        spindexerWheel.Stop();
        spindexerCone.Stop();
        tunnelFeeder1.Stop();
        tunnelFeeder2.Stop();
        shooterFlywheel.SetRpm(900f);
        hoodTarget = 0f;
        intakeDeployed = false;
        intakeWobble = false;
        shootingTimer = 0f;
    }

    protected override bool Aligned()
    {
        bool useNZ = !InAllianceZone() || AutoPass;
        float currentTurretTolerance = useNZ ? turretAngleToleranceNeutral : turretAngleTolerance;
        float currentHoodTolerance = useNZ ? hoodAtTargetToleranceNeutral : hoodAtTargetTolerance;

        bool turretAligned = Mathf.Abs(turret.GetError()) < currentTurretTolerance;
        bool hoodAligned = Mathf.Abs(hood.GetAngle() - hoodTarget) < currentHoodTolerance;
        bool flywheelAtSpeed = shooterFlywheel.AtSpeed();

        return turretAligned && hoodAligned && flywheelAtSpeed;
    }

    protected override void AllianceZone()
    {
        Shooter();
    }

    protected override void NeutralZone()
    {
        Shooter();
    }

    protected override void Shooting()
    {
        Shooter();
    }

    protected override void Intaking()
    {
        Intake();
    }

    protected override void HandleSwm()
    {
        if (rb != null && swmRef != null && swmRef.parent != null)
        {
            base.HandleSwm();
        }
    }

    private void UpdateDriveSpeed()
    {
        bool isShooting = WantsToShoot() && InAllianceZone() && !AutoPass;
        bool isPassing = (WantsToShoot() && !InAllianceZone()) || AutoPass;

        float driveMp = 1f;
        float turnMp = 1f;

        if (isShooting)
        {
            driveMp = shootingDriveMultiplier;
            turnMp = shootingTurnMultiplier;
        }

        if (isPassing)
        {
            driveMp = passingDriveMultiplier;
            turnMp = passingTurnMultiplier;
        }

        driveController.SetDriveMp(driveMp);
        driveController.SetTurnMp(turnMp);
    }
}