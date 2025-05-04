using System;
using Unity.Behavior;
using Unity.Properties;
using UnityEngine;
using Action = Unity.Behavior.Action;

[Serializable, GeneratePropertyBag]
[NodeDescription(name: "CheckEnemy", story: "Check [enemy] in [radius]", category: "Miro", id: "e386761779fa2a42b724b158d0467c0d")]
public partial class CheckEnemyAction : Action
{
    [SerializeReference] public BlackboardVariable<GameObject> Enemy;
    [SerializeReference] public BlackboardVariable<float> Radius;
    [SerializeReference] public BlackboardVariable<Transform> Soldier;

    protected override Status OnStart()
    {
        return Status.Running;
    }

    protected override Status OnUpdate()
    {
        Collider2D[] colliderInRadius = Physics2D.OverlapCircleAll(Soldier.Value.position, Radius);
        foreach (Collider2D collider in colliderInRadius)
        {
            if (collider.tag == "Enemy" && Soldier.Value != collider.transform)
            {
                Enemy.Value = collider.gameObject;
                return Status.Success;
            }
        }
        //Debug.Log(colliderInRadius.Length);
        return Status.Running;
    }

    protected override void OnEnd()
    {
    }
}

