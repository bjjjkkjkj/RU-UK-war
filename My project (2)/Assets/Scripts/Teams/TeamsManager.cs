using System.Collections;
using UnityEngine;

public class TeamsManager : MonoBehaviour
{
    public TeamController[] Teams;
    public float TimeSpawn;
    private void Start()
    {
        StartCoroutine(TeamsSpawner());
    }
    private IEnumerator TeamsSpawner()
    {
        while (true)
        {
            print("спавним людей");
            foreach (TeamController team in Teams)
            {
                team.Spawn();
            }
            yield return new WaitForSeconds(1);
        }
    }

}
