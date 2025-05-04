using System;
using Unity.Behavior;
using Unity.Properties;
using UnityEngine;
using Action = Unity.Behavior.Action;

[Serializable, GeneratePropertyBag]
[NodeDescription(name: "AttackEnemy", story: "soldier attack [enemy]", category: "Action", id: "ee2b6781ef592a3af017f7d0e5dfea83")]
public partial class AttackEnemyAction : Action
{
    [SerializeReference] public BlackboardVariable<GameObject> Enemy;
    [SerializeReference] public BlackboardVariable<GameObject> Soldier;
    [SerializeReference] public BlackboardVariable<WeaponInfo> GuninHands;



    protected override Status OnStart()
    {
        return Status.Running;
    }

    protected override Status OnUpdate()
    {
        if (Enemy.Value == null)
        {
            return Status.Failure;
        }

        Vector3 lookDirection = Enemy.Value.transform.position - Soldier.Value.transform.position;
        lookDirection.Normalize();
        float angle = Mathf.Atan2(lookDirection.y, lookDirection.x) * Mathf.Rad2Deg;
        Soldier.Value.transform.rotation = Quaternion.Euler(0, 0, angle - 90);
        GuninHands.Value.Attack();
        return Status.Success;
    }

    protected override void OnEnd()
    {
    }
}

