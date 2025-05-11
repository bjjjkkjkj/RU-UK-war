using System.Collections.Generic;
using Sirenix.OdinInspector;
using UnityEngine;

public class CaptureZone : SerializedMonoBehaviour
{
    [SerializeField] private float maxPoints = 10;
    [SerializeField] private Dictionary<string, float> teamsPoints;
    [SerializeField] private Dictionary<string, int> membersInZone;

    private void Start()
    {
        membersInZone = new();
        teamsPoints = new();
    }

    private void Update()
    {
        //capturingPoints += Time.deltaTime * membersInZone.Count;
        //
        //if (capturingPoints > maxPoints)
        //{
        //
        //}
    }

    private void OnTriggerEnter2D(Collider2D collision)
    {
        if (collision.TryGetComponent(out TeamMember newMember))
        {
            if (!teamsPoints.ContainsKey(newMember.TeamName))
            {
                teamsPoints.Add(newMember.TeamName, 0);
            }
        }
    }

    private void OnTriggerExit2D(Collider2D collision)
    {
        if (collision.TryGetComponent(out TeamMember oldMember))
        {

        }
    }
}
