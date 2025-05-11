using Pathfinding;
using System;
using Unity.Behavior;
using UnityEngine;
using Action = Unity.Behavior.Action;
using Unity.Properties;

[Serializable, GeneratePropertyBag]
[NodeDescription(name: "MoveToCaptureZone", story: "Move [agent] to zone", category: "Action", id: "6f6c9a1fd77b423e5e0e9490a1a1740c")]
public partial class MoveToCaptureZoneAction : Action
{
    [SerializeReference] public BlackboardVariable<AIPath> Agent;

    protected override Status OnStart()
    {
        return Status.Running;
    }

    protected override Status OnUpdate()
    {
        return Status.Success;
    }

    protected override void OnEnd()
    {
    }
}

