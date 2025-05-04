using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Bullet : MonoBehaviour
{
    public float damage;
   
    void Start()
    {
        Destroy(gameObject,4);

    }
    void OnTriggerEnter2D(Collider2D collision)
    {
        if(collision.TryGetComponent(out Health health)){
        health.DealDamage(damage);
        
        }
        Destroy(gameObject);
    }

}
