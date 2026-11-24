# Clockwork 4322 Rebuilt Robot Mod - Implementation Plan

## Overview
Add FRC Team 4322's 2026 "Clockwork" robot to MoSim's **Rebuilt** game (2026 season, based on 2017 Steamworks). The robot is a **single corner turret** with **adjustable hood**, **spindexer intake**, and **shoot-on-move** capability. Game piece is **Fuel** (balls).

## Robot Configuration
- **Game**: Rebuilt (2026) - uses `RebuiltTurretRobot` (already set in Clockwork.cs)
- **Turret**: Single corner-mounted turret (azimuth via `GenericTurret` + `GenericJoint`)
- **Hood**: Adjustable elevation via `GenericJoint`
- **Shooter**: Flywheel via `RollerGroup`
- **Ball Path**: Intake rollers → Spindexer (wheel + cone) → 2× tunnel feeders → Turret flywheels
- **SOTM**: Velocity-dampened targeting (like 1706 RatchetRockers)
- **Game Piece**: Fuel (single type, balls)

---

## Phase 1: Project Structure & Setup (Already Done)

### 1.1 Verify Existing Structure
```
Assets/Prefabs/Games/Rebuilt/Robots/Mods/4322 Clockwork/
├── Clockwork.asmdef              ✓ exists
├── Clockwork.cs                  ✓ exists (inherits RebuiltTurretRobot)
├── Clockwork/Clockwork.prefab    ✓ exists (model/colliders/joints)
├── Clockwork/Clockwork.asset     ✓ exists (robot metadata)
├── Clockwork/ClockworkMainMenu.prefab
├── Clockwork/ClockworkSpawnSelect.prefab
└── Clockwork/TutorialCards/
```

### 1.2 Verify Prefab Hierarchy
Ensure the prefab has these key GameObjects with `GenericJoint` components:
- `TurretBase` - azimuth joint (Y-axis) + `GenericTurret` component
- `Hood` - elevation joint (X or Z axis)
- `ShooterFlywheel` - `RollerGroup`
- `IntakeRollers` - `RollerGroup`
- `SpindexerWheel` - `RollerGroup` (spinning wheel)
- `SpindexerCone` - `RollerGroup` (cone on top)
- `TunnelFeeder1` - `RollerGroup` (first tunnel stage)
- `TunnelFeeder2` - `RollerGroup` (second tunnel stage)
- `SWMRef` - Transform child of root (for shoot-on-move)

---

## Phase 2: Core Robot Script (`Clockwork.cs`)

### 2.1 Class Structure (Update Existing)
```csharp
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
    [SerializeField] private RollerGroup hoodRollers; // optional, for ball control during hood movement
    
    [Header("PIDs")]
    [SerializeField] private PidConstants turretPid;
    [SerializeField] private PidConstants hoodPid;
    
    [Header("SOTM")]
    [SerializeField] private Transform swmRef;
    [SerializeField] private SOTMSettings swmSettings;
    [SerializeField] private float velocityDampeningValue;
    [SerializeField] private float timeOfFlight;
    
    [Header("Shooter Tuning")]
    [SerializeField] private RebuiltInterpolation azimuthFlywheelInterpolation; // distance → RPM
    [SerializeField] private RebuiltInterpolation azimuthHoodInterpolation;     // distance → hood angle
    [SerializeField] private RebuiltInterpolation azimuthTofInterpolation;      // distance → time of flight
    [SerializeField] private RebuiltInterpolation neutralFlywheelInterpolation;
    [SerializeField] private RebuiltInterpolation neutralHoodInterpolation;
    [SerializeField] private RebuiltInterpolation neutralTofInterpolation;
    
    [Header("Thresholds")]
    [SerializeField] private float turretAngleTolerance = 15f;
    [SerializeField] private float turretAngleToleranceNeutral = 25f;
    [SerializeField] private float hoodAtTargetTolerance = 0.3f;
    [SerializeField] private float hoodAtTargetToleranceNeutral = 0.8f;
    [SerializeField] private float hoodRollerReduction = 0.5f;
    
    [Header("Intake Tuning")]
    [SerializeField] private float intakeRpm = 2000f;
    [SerializeField] private float spindexerWheelRpm = 3000f;
    [SerializeField] private float spindexerConeRpm = 1500f;
    [SerializeField] private float tunnelFeeder1Rpm = 4000f;
    [SerializeField] private float tunnelFeeder2Rpm = 5000f;
    [SerializeField] private float uptakeRpm = 3000f; // feeder speed when shooting
    
    // Runtime state
    private Rigidbody rb;
    private bool isShooting;
}
```

### 2.2 Key Methods to Implement

| Method | Purpose |
|--------|---------|
| `Init()` | Initialize joints, PIDs, interpolations, SOTM, scheduler |
| `UpdatePids()` | Update turret/hood PIDs (different for wrapping vs normal) |
| `UpdateJoints()` | Apply targets to hood joint, call turret.UpdateTurretJoints() |
| `HandleSwm()` | Update SWM reference point with velocity damping (inherited) |
| `AllianceZone()` | Auto-aim at hub, set shooter/hood targets for fuel |
| `NeutralZone()` | Pass targeting (closest pass node), set shooter/hood |
| `Intaking()` | Run intake + spindexer + tunnel feeders (no shooter) |
| `IntakingAndShooting()` | Run full ball path + shooter feed when aligned |
| `Shooting()` | Run shooter + hood + feeders (no intake) |
| `Feed()` | Override feeder speeds when `Aligned()` true |
| `Idling()` | Stop all rollers, stow mechanisms |
| `Aligned()` | Check turret error + hood at target + flywheel at speed |

### 2.3 Scheduler Registration (in Init)
```csharp
protected override void Init()
{
    base.Init(); // Registers HandleSwm, zone dispatch, shooter-state dispatch
    
    // Initialize PIDs
    turret.SetPid(turretPid);
    hood.SetPid(hoodPid);
    
    // Configure turret wrapping
    turret.LeftHood.noWrap = true;
    turret.LeftHood.noWrapAngle = 300f; // or appropriate limit
    
    // Initialize interpolations
    azimuthFlywheelInterpolation.Initialize(this);
    azimuthHoodInterpolation.Initialize(this);
    azimuthTofInterpolation.Initialize(this);
    neutralFlywheelInterpolation.Initialize(this);
    neutralHoodInterpolation.Initialize(this);
    neutralTofInterpolation.Initialize(this);
    
    // Register subsystems
    Scheduler.AddSubsystems(Intake, Hopper, Shooter, TurretTrack, UpdateValues);
    Scheduler.AddProtectedSystem(UpdateJoints, UpdateDriveSpeed, turret.SubsystemLoop);
    Scheduler.AddLateRecursive(UpdatePids);
    Scheduler.AddRecursive(turret.HandleDampedVelocity);
}
```

---

## Phase 3: Mechanism Implementation Details

### 3.1 Turret (GenericTurret via DualTurretWrapper pattern)
Since this is a **single turret**, use `GenericTurret` directly (not `DualTurretWrapper`). Reference: `GenericTurret.cs` handles auto-align, wrapping, SOTM.

- **Axis**: Y (azimuth)
- **Reference Point**: Turret pivot transform (center of rotation)
- **Wrapping**: `useNoWrap = true`, min/max based on physical limits (e.g., -270 to +270)
- **Auto-Align**: 
  - Alliance Zone: `SetDefaultMap(GenericTurret.DefaultMap.Hub)` - aims at hub
  - Neutral Zone: `SetPassMapClosest()` - aims at nearest pass node
- **SOTM**: Predict target position using robot velocity × time of flight

### 3.2 Hood (GenericJoint)
- **Axis**: X or Z (elevation)
- **PID**: Position control with `SetTargetAngle()`
- **Range**: 0° to ~35° (adjustable hood)
- **Rollers**: Optional `hoodRollers` for ball control during hood movement (RPM = flywheelRPM / hoodRollerReduction)

### 3.3 Shooter Flywheel (RollerGroup)
- **Control**: `SetRpm(targetRpm)` from interpolation map
- **Speed Check**: `AtSpeed()` for `Aligned()` check

### 3.4 Ball Path (RollerGroups)
| Stage | Component | RPM (tunable) | Notes |
|-------|-----------|---------------|-------|
| Intake | `intakeRollers` | `intakeRpm` (2000) | Ground pickup |
| Spindexer Wheel | `spindexerWheel` | `spindexerWheelRpm` (3000) | Main spinning wheel |
| Spindexer Cone | `spindexerCone` | `spindexerConeRpm` (1500) | Cone on top, slower |
| Tunnel Feeder 1 | `tunnelFeeder1` | `tunnelFeeder1Rpm` (4000) | Accelerating |
| Tunnel Feeder 2 | `tunnelFeeder2` | `tunnelFeeder2Rpm` (5000) | Final feed to shooter |
| Shooter | `shooterFlywheel` | Interpolated | Distance-dependent |
| Hood Rollers | `hoodRollers` | flywheelRPM / `hoodRollerReduction` | If present |

**Intake Logic** (in `Intake()` subsystem):
- `IntakeAction` pressed → run all intake path rollers at configured RPMs
- `OuttakeAction` (AutoShoot) + `Aligned()` → `Feed()` overrides feeder speeds to `uptakeRpm`
- `IntakeAction` + `OuttakeAction` → `IntakingAndShooting()` runs full path

### 3.5 Interpolation Maps (Inspector Arrays)
Create `DistanceValue[]` arrays in Inspector for:
- **AZ Flywheel**: distance (inches) → RPM
- **AZ Hood**: distance → hood angle (degrees)
- **AZ TOF**: distance → time of flight (seconds)
- **NZ Flywheel**: pass distance → RPM
- **NZ Hood**: pass distance → hood angle
- **NZ TOF**: pass distance → time of flight

---

## Phase 4: Rebuilt-Specific Implementation

### 4.1 Input Actions (from RebuiltRobotBase)
- `IntakeAction` - "Intake" binding
- `OuttakeAction` - "AutoShoot" binding  
- `StowAction` - "Stow" binding
- `ManualShoot` - "ManualShoot" binding
- `AutoPass` - "AutoPass" binding
- `ClimbAction` - "Climb" binding
- `FaceDown` - "FaceDown" binding
- `RobotSpecial` / `RobotSpecial2` / `RobotSpecial3` - special functions

### 4.2 Game Piece Controller Setup (in Start)
```csharp
protected override void Start()
{
    base.Start();
    
    // Rebuilt uses single game piece type: Fuel
    var fuelController = RobotGamePieceController.GetPieceByName("Fuel");
    fuelController.intakes.Add(fuelIntake); // RebuiltGamePieceIntake component
    
    // Set preload if needed
    // RobotGamePieceController.SetPreload(fuelStowState);
}
```

### 4.3 Fuel Intake Component
Add `RebuiltGamePieceIntake` component to intake collider area.

---

## Phase 5: Subsystem Implementations

### 5.1 TurretTrack Subsystem
```csharp
private void TurretTrack()
{
    bool inAllianceZone = InAllianceZone();
    bool isPassing = AutoPass || !inAllianceZone;
    
    if (isPassing)
    {
        turret.SetPassMapClosest();
        turret.SetCustomTarget(turret.GetShootOnMoveTarget(turret.GetCurrentTarget()));
    }
    else
    {
        turret.SetDefaultMap(GenericTurret.DefaultMap.Hub);
        turret.SetCustomTarget(turret.GetShootOnMoveTarget(turret.GetCurrentTarget()));
    }
    
    turret.Align();
}
```

### 5.2 Shooter Subsystem
```csharp
private void Shooter()
{
    SetInterpMap(); // Switch between AZ/NZ maps based on zone
    
    bool useNZ = !InAllianceZone() || AutoPass;
    float currentTurretTolerance = useNZ ? turretAngleToleranceNeutral : turretAngleTolerance;
    
    // Get hood angle from interpolation
    var hoodInterp = useNZ ? neutralHoodInterpolation : azimuthHoodInterpolation;
    hoodInterp.SetCustomTarget(turret.GetShootOnMoveTarget(hoodInterp.GetCurrentTarget()));
    float hoodAngle = hoodInterp.Interpolate();
    
    // Get flywheel RPM from interpolation
    var flywheelInterp = useNZ ? neutralFlywheelInterpolation : azimuthFlywheelInterpolation;
    flywheelInterp.SetCustomTarget(turret.GetShootOnMoveTarget(flywheelInterp.GetCurrentTarget()));
    float shooterSpeed = flywheelInterp.Interpolate();
    
    // Check under trench (from 1706 pattern)
    bool underTrench = UnderTrench(turret.transform, 0.17f);
    
    // Set shooter and hood
    turret.SetShooterAndHood(
        turret.GetError() < currentTurretTolerance ? shooterSpeed : 0f,
        underTrench ? 0f : Mathf.Clamp(hoodAngle, 0f, 33f)
    );
    
    // Handle feeder rollers
    bool readyToShoot = (useNZ || turret.ShooterAndHoodAtTarget()) && 
                        !underTrench && 
                        turret.GetError() < currentTurretTolerance;
    
    if (readyToShoot || ManualShoot)
    {
        tunnelFeeder2.SetRpm(uptakeRpm);
        tunnelFeeder1.SetRpm(uptakeRpm);
    }
    else
    {
        tunnelFeeder2.OverrideRpm(-1000); // Reverse to prevent jamming
        tunnelFeeder1.OverrideRpm(-1000);
    }
    
    if (ManualShoot)
    {
        turret.SetShooterAndHood(manualShotRpm, 17.8f);
    }
}
```

### 5.3 Feed Subsystem (called after Shooter when Aligned)
```csharp
protected override void Feed()
{
    // Override feeder speeds for actual feeding
    tunnelFeeder2.SetRpm(uptakeRpm);
    tunnelFeeder1.SetRpm(uptakeRpm);
}
```

### 5.4 Intake Subsystem
```csharp
private void Intake()
{
    if (IntakeAction)
    {
        intakeRollers.SetRpm(intakeRpm);
        spindexerWheel.SetRpm(spindexerWheelRpm);
        spindexerCone.SetRpm(spindexerConeRpm);
        tunnelFeeder1.SetRpm(tunnelFeeder1Rpm);
        tunnelFeeder2.SetRpm(tunnelFeeder2Rpm);
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
```

### 5.5 IntakingAndShooting Subsystem
```csharp
protected override void IntakingAndShooting()
{
    Intake(); // Run intake path
    Shooter(); // Run shooter (will handle feeders)
}
```

### 5.6 Idling Subsystem
```csharp
protected override void Idling()
{
    intakeRollers.Stop();
    spindexerWheel.Stop();
    spindexerCone.Stop();
    tunnelFeeder1.Stop();
    tunnelFeeder2.Stop();
    shooterFlywheel.SetRpm(-2000); // Slow reverse to prevent overheating
    turret.SetShooterAndHood(-2000, 0f);
}
```

### 5.7 Aligned Check
```csharp
protected override bool Aligned()
{
    bool useNZ = !InAllianceZone() || AutoPass;
    float currentTurretTolerance = useNZ ? turretAngleToleranceNeutral : turretAngleTolerance;
    float currentHoodTolerance = useNZ ? hoodAtTargetToleranceNeutral : hoodAtTargetTolerance;
    
    bool turretAligned = Math.Abs(turret.GetError()) < currentTurretTolerance;
    bool hoodAligned = Math.Abs(hood.GetAngle() - hoodTarget) < currentHoodTolerance;
    bool flywheelAtSpeed = shooterFlywheel.AtSpeed();
    
    return turretAligned && hoodAligned && flywheelAtSpeed;
}
```

### 5.8 UpdateJoints Subsystem
```csharp
private void UpdateJoints()
{
    // Apply hood target
    hood.SetTargetAngle(hoodTarget).withAxis(hoodJointAxis);
    
    // Update turret joints
    turret.UpdateTurretJoints();
}
```

### 5.9 UpdateValues Subsystem (SOTM TOF, tolerances)
```csharp
private void UpdateValues()
{
    bool useNZ = !InAllianceZone() || AutoPass;
    float currentHoodTolerance = useNZ ? hoodAtTargetToleranceNeutral : hoodAtTargetTolerance;
    
    // Update turret values
    turret.UpdateValues(velocityDampeningValue, currentHoodTolerance, hoodRollerReduction);
    
    // Update time of flight (3 samples for smoothing)
    for (int i = 0; i < 3; i++)
    {
        float tof = GetShootOnMoveTof();
        turret.UpdateTimeOfFlight(tof);
    }
}

private float GetShootOnMoveTof()
{
    var tofInterp = useNZ ? neutralTofInterpolation : azimuthTofInterpolation;
    
    if (useNZ)
    {
        tofInterp.SetPassMapClosest();
    }
    else
    {
        tofInterp.SetDefaultMap(GenericTurret.DefaultMap.Hub);
    }
    
    tofInterp.SetCustomTarget(turret.GetShootOnMoveTarget(tofInterp.GetCurrentTarget()));
    return tofInterp.Interpolate();
}
```

### 5.10 UpdateDriveSpeed Subsystem (slow down when shooting)
```csharp
private void UpdateDriveSpeed()
{
    bool isShooting = WantsToShoot() && InAllianceZone() && !AutoPass;
    bool isPassing = (WantsToShoot() && !InAllianceZone()) || AutoPass;
    
    float driveMp = 1f;
    float turnMp = 1f;
    
    if (isShooting)
    {
        driveMp = shootingDriveMultiplier;    // e.g., 0.12f
        turnMp = shootingTurnMultiplier;      // e.g., 0.2f
    }
    if (isPassing)
    {
        driveMp = passingDriveMultiplier;     // e.g., 0.6f
        turnMp = passingTurnMultiplier;       // e.g., 0.3f
    }
    
    driveController.SetDriveMp(driveMp);
    driveController.SetTurnMp(turnMp);
}

private bool WantsToShoot()
{
    return OuttakeAction || AutoPass || ManualShoot;
}
```

---

## Phase 6: Prefab & Inspector Setup

### 6.1 Required Components on Prefab Root
- `Clockwork` (script)
- `Rigidbody` (on root)
- `BoxCollider` (bumper collider on root)
- `RebuiltRobotGamePieceController` (on root)
- `DriveController` (on root)

### 6.2 Configure Each Joint in Inspector
- **TurretBase**: Add `GenericTurret` + `GenericJoint` (Y-axis, noWrap=true, min/max limits)
- **Hood**: Add `GenericJoint` (X or Z axis, limited range 0-35°)
- **All RollerGroups**: Assign correct wheel transforms, set RPM signs

### 6.3 SWM Reference
- Create child Transform `SWMRef` under root
- Assign to `swmRef` field
- Create `SOTMSettings` asset, assign to `swmSettings`

---

## Phase 7: Testing & Validation

### 7.1 Unit Tests (Manual)
- [ ] Turret rotates to target (hub in AZ, pass node in NZ)
- [ ] Hood moves to interpolated angle for distance
- [ ] Flywheel reaches target RPM
- [ ] Intake rollers spin when Intake held
- [ ] Full ball path runs: intake → spindexer → tunnel → shooter
- [ ] `Aligned()` returns true when turret error < tolerance AND hood at target AND flywheel at speed
- [ ] SOTM: turret leads target when driving
- [ ] Wrapping: turret unwinds correctly at limits
- [ ] Disabled state: joints brake/lock properly
- [ ] Drive speed reduces when shooting/passing

### 7.2 Integration Tests
- [ ] Robot spawns in Rebuilt match
- [ ] Input mappings work (Intake, AutoShoot, ManualShoot, Stow, Climb)
- [ ] Fuel game pieces interact correctly
- [ ] Scoring works (hub, climb)
- [ ] Auto-align aims at hub correctly
- [ ] Tutorial cards display correctly

### 7.3 Performance
- [ ] FixedUpdate subsystems complete in < 2ms
- [ ] No physics instability at high RPM
- [ ] No memory leaks (Scheduler subscriptions cleaned up)

---

## Phase 8: Polish & Release

### 8.1 Assets
- [ ] Robot thumbnail render (4322Render.png)
- [ ] Main menu prefab with team branding
- [ ] Spawn selection prefab
- [ ] Tutorial cards (3 minimum)
- [ ] Workshop metadata asset

### 8.2 Documentation
- [ ] README with controls
- [ ] Tuning guide for PIDs/interpolations

---

## Dependencies & References

### Existing Code to Reference
| File | Purpose |
|------|---------|
| `Assets/Scripts/Games/Rebuilt/Robots/RebuiltTurretRobot.cs` | Base class (already inherited) |
| `Assets/Scripts/Games/Rebuilt/Robots/1706/RatchetRockers.cs` | Dual turret + SOTM reference (adapt for single turret) |
| `Assets/Scripts/RobotFramework/Components/GenericTurret.cs` | Turret control |
| `Assets/Scripts/RobotFramework/Components/GenericJoint.cs` | Joint PID control |
| `Assets/Scripts/RobotFramework/Components/RollerGroup.cs` | Roller control |
| `Assets/Scripts/Games/Rebuilt/Components/RebuiltInterpolation.cs` | Distance-based interpolation |
| `Assets/Scripts/Games/Rebuilt/GamePieceSystem/RebuiltGamePieceIntake.cs` | Fuel intake component |

### Files to Modify
1. `Clockwork.cs` - Main robot script (implement all TODOs)

### Files to Verify/Configure
1. `Clockwork/Clockwork.prefab` - Wire all serialized fields
2. `Clockwork/Clockwork.asset` - Verify metadata
3. `Clockwork.asmdef` - Verify assembly references

---

## Risk Mitigation

| Risk | Mitigation |
|------|------------|
| Turret wrapping instability | Test `noWrap` limits thoroughly; use `GenericTurret`'s built-in unwind logic |
| Ball jamming in spindexer/tunnel | Tune roller RPMs progressively (intake < spindexer < tunnel < shooter) |
| SOTM prediction error | Calibrate `velocityDampeningValue` and `timeOfFlight` per distance |
| Fuel game piece detection | Verify `RebuiltGamePieceIntake` triggers match collider setup |
| Physics explosion at high RPM | Limit max RPM, use `RollerGroup` slip settings, verify rigidbody mass |

---

## Implementation Order

1. **Clockwork.cs skeleton** (1 hr): Fields, Init, scheduler registration
2. **Turret + Hood** (2 hr): Auto-align, SOTM, interpolation wiring
3. **Ball path rollers** (1.5 hr): Intake → spindexer → tunnel → shooter logic
4. **Rebuilt integration** (1 hr): Game piece controller, input mapping, Feed/Aligned
5. **Prefab wiring** (1 hr): Assign all serialized fields in Inspector
6. **Testing & tuning** (2-4 hr): Iterate on PIDs, interpolation maps, RPMs
7. **Polish** (1 hr): Menu, spawn, tutorial cards, thumbnail

**Total**: ~10-12 hours