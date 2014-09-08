using System;
using System.Collections.Generic;
using UnityEngine;
using KSP;

public class ModuleRCSFX : ModuleRCS
{
    [KSPField()]
    string runningEffectName = "running_closed";

    [KSPField()]
    string engageEffectName = "engage";

    [KSPField()]
    string flameoutEffectName = "flameout";

    [KSPField()]
    public bool useZaxis;

    [KSPField()]
    public bool rcs_active;

    public float mixtureFactor;

    private List<Propellant> _props;
    public List<Propellant> propellants
    {
        get
        {
            if (_props == null)
                _props = new List<Propellant>();
            return _props;
        }

    }

    public override void OnLoad(ConfigNode node)
    {
        ModuleRCSFX prefab = null;
        if (part != null && part.partInfo != null)
            prefab = (ModuleRCSFX)part.partInfo.partPrefab.Modules["ModuleRCSFX"];

        if (prefab != null && prefab.propellants != null)
            _props = prefab.propellants;
        else
            LoadPropellants(node);
    }

    public override void OnStart(StartState state)
    {
        base.OnStart(state);
        SetupPropellant();
    }

    public float getMaxFuelFlow(Propellant p)
    {
        return p.ratio * this.thrusterPower / (atmosphereCurve.Evaluate(0f) * 9.82f) / this.resourceMass;
    }

    public override string GetInfo()
    {
        string info = "<b>Thruster Power: </b>" + thrusterPower.ToString("F") + "\n";
        info += "<b>Thruster ISP: </b>" + atmosphereCurve.Evaluate(1f).ToString("F0")
              + " (ASL) - " + atmosphereCurve.Evaluate(0f).ToString("F0") + "(Vac)\n";
        info += "<color=#99ff00ff><b>Requires:</b></color> \n";
        foreach (Propellant p in propellants)
            info += "<b>" + p.name + ": </b>" + getMaxFuelFlow(p).ToString("F4") + "/sec. Max. \n";
        return info;
    }

    Vector3 inputLinear;
    Vector3 inputAngular;
    bool precision;

    new public void Update()
    {
        if (this.part.vessel == null)
            return;

        inputLinear = vessel.ReferenceTransform.rotation * new Vector3(vessel.ctrlState.X, vessel.ctrlState.Z, vessel.ctrlState.Y);
        inputAngular = vessel.ReferenceTransform.rotation * new Vector3(vessel.ctrlState.pitch, vessel.ctrlState.roll, vessel.ctrlState.yaw);
        precision = FlightInputHandler.fetch.precisionMode;
    }

    new public void FixedUpdate()
    {
        if (HighLogic.LoadedSceneIsEditor)
            return;

        if (TimeWarp.CurrentRate > 1.0f && TimeWarp.WarpMode == TimeWarp.Modes.HIGH)
        {
            foreach (FXGroup fx in thrusterFX)
            {
                fx.setActive(false);
                fx.Power = 0f;
            }
            return;
        }


        realISP = atmosphereCurve.Evaluate((float)vessel.staticPressure);
        thrustForces.Clear();
        if (isEnabled && part.isControllable)
        {
            if (vessel.ActionGroups[KSPActionGroup.RCS] != rcs_active)
            {
                rcs_active = vessel.ActionGroups[KSPActionGroup.RCS];
                part.Effect(engageEffectName, 1.0f);
                part.Effect(runningEffectName, 0f);
                foreach (FXGroup fx in thrusterFX)
                {
                    fx.setActive(false);
                    fx.Power = 0f;
                }
            }
            if (vessel.ActionGroups[KSPActionGroup.RCS])
            {
                Vector3 CoM = vessel.CoM + vessel.rb_velocity * Time.deltaTime;

                float effectPower = 0f;
                for (int i = 0; i < thrusterTransforms.Count; i++)
                {
                    if (thrusterTransforms[i].position != Vector3.zero)
                    {
                        Vector3 torque = Vector3.Cross(inputAngular, (thrusterTransforms[i].position - CoM).normalized);
                        Vector3 thruster;
                        if (useZaxis)
                            thruster = thrusterTransforms[i].forward;
                        else
                            thruster = thrusterTransforms[i].up;
                        float thrust = Mathf.Max(Vector3.Dot(thruster, torque), 0f);
                        thrust += Mathf.Max(Vector3.Dot(thruster, inputLinear), 0f);
                        if (thrust > 0.0001f)
                        {
                            if (precision)
                            {
                                float arm = GetLeverDistance(-thruster, CoM);
                                if (arm > 1.0f)
                                    thrust = thrust / arm;
                            }
                            thrust = Mathf.Clamp(thrust, 0f, 1f);
                            thrustForces.Add(thrust);

                            if (!isJustForShow)
                            {

                                thrust = FuelLimitedThrust(thrust);

                                Vector3 force = (-thrusterPower * thrust) * thruster;
                                Vector3 position = thrusterTransforms[i].transform.position;
                                part.Rigidbody.AddForceAtPosition(force, position, ForceMode.Force);
                            }

                            thrusterFX[i].Power = Mathf.Clamp(thrust, 0.1f, 1f);
                            if (effectPower < thrusterFX[i].Power)
                                effectPower = thrusterFX[i].Power;
                            thrusterFX[i].setActive(thrust > 0f);
                        }
                        else
                        {
                            thrusterFX[i].setActive(false);
                            thrusterFX[i].Power = 0f;
                        }
                    }
                }
                part.Effect(runningEffectName, effectPower);
            }
        }
        else
        {
            foreach (FXGroup fx in thrusterFX)
            {
                fx.setActive(false);
                fx.Power = 0f;
            }
        }

    }

    public void LoadPropellants(ConfigNode node)
    {
        propellants.Clear();
        foreach (ConfigNode prop in node.GetNodes("PROPELLANT"))
        {
            Propellant fuel = new Propellant();
            fuel.Load(prop);
            propellants.Add(fuel);
        }
        SetupPropellant();
    }

    public void SetupPropellant()
    {
        mixtureFactor = 0.0f;
        resourceMass = 0.0f;
        foreach (Propellant fuel in propellants)
        {
            mixtureFactor += fuel.ratio;
            resourceMass += fuel.ratio * PartResourceLibrary.Instance.GetDefinition(fuel.name).density;
        }

    }

    public float FuelLimitedThrust(float thrust)
    {
        if (CheatOptions.InfiniteRCS)
            return thrust;
        foreach (Propellant propellant in propellants)
        {

            float fuelAmount = (thrust * Time.deltaTime) / (this.resourceMass * realISP * 9.82f);
            float requestedAmount = fuelAmount * propellant.ratio;
            float actualAmount = part.RequestResource(propellant.id, requestedAmount);
            if (actualAmount < requestedAmount)
            {
                thrust *= actualAmount / requestedAmount;
                part.Effect(flameoutEffectName, 1.0f);
            }
        }
        return thrust;
    }

}
