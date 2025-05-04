using UnityEngine;

public class Health : MonoBehaviour
{
    public float health;
    public void DealDamage(float damage)
    {
        health -= damage;
        if (health <= 0)
        {
            Destroy(gameObject);
        }

    }
}//tyt konec
