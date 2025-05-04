using System.Collections;
using System.Collections.Generic;
using Pathfinding;
using UnityEngine;

public class Enemy : MonoBehaviour
{   
    public Transform player;
    public AIPath agent;
     void Update()
    {
        agent.destination=player.position;
    }
}
