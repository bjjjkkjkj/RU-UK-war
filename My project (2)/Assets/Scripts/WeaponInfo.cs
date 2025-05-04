using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

public class WeaponInfo : MonoBehaviour
{
    public float speedAttack;
    public float radiusAttack;
    public float damage;
    public Transform attackPoint;
    public float attackForce;
    public GameObject bulletPrefabb;


    bool canAttack;
    private void Start() {
        canAttack=true;
    }

 public void Attack()
{
    if(canAttack==true){
        GameObject newPullet=Instantiate(bulletPrefabb,attackPoint.position,Quaternion.identity);
        Rigidbody2D rb=newPullet.GetComponent<Rigidbody2D>();
        Bullet bullet= newPullet.GetComponent<Bullet>();
        bullet.damage=damage;
        rb.AddForce(-attackPoint.up*attackForce,ForceMode2D.Impulse);
        canAttack=false;
        Invoke("ResetAttack",speedAttack);
    
    }
}
 void ResetAttack(){
    canAttack=true;
 }
 
 

}
