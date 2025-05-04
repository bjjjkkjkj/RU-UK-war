using System.Collections.Generic;
using UnityEngine;

public class TeamController : MonoBehaviour
{
    public string TeamName;
    public Transform[] SpawnPoints;
    public float Resuors;
    public int MaxPeople;
    public List<TeamMember> Soldiers;
    public TeamMember SoldierPrefab;
    public void Spawn()
    {

        int neededCount = MaxPeople - Soldiers.Count;
        for (int i = 0; i < neededCount; i++)
        {
            Transform randomPoint = SpawnPoints[Random.Range(0, SpawnPoints.Length)];
            TeamMember newSoldier = Instantiate(SoldierPrefab, randomPoint.position, Quaternion.identity);
            Soldiers.Add(newSoldier);
            newSoldier.TeamName = TeamName;
        }
    }
}
