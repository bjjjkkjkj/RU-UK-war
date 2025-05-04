using Pathfinding;
using System;
using Unity.Behavior;
using UnityEngine;
using Action = Unity.Behavior.Action;
using Unity.Properties;

[Serializable, GeneratePropertyBag]
[NodeDescription(name: "MoveToEnemy", story: "Move [soldier] to [enemy]", category: "Action", id: "46f005a50329c04113a2be3876a5f035")]
public partial class MoveToEnemyAction : Action
{
    [SerializeReference] public BlackboardVariable<AIPath> Soldier;
    [SerializeReference] public BlackboardVariable<GameObject> Enemy;

    protected override Status OnStart()
    {
        Soldier.Value.isStopped = false;
        return Status.Running;
    }

    protected override Status OnUpdate()
    {
        Soldier.Value.destination = Enemy.Value.transform.position;
        if (Vector2.Distance(Enemy.Value.transform.position, Soldier.Value.transform.position) <= 3)
        {
            return Status.Success;
            
        }
        return Status.Running;
    }

    protected override void OnEnd()
    {
        Soldier.Value.isStopped = true;
    }
}

