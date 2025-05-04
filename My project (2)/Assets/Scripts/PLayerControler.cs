using UnityEngine;

public class PLayerControler : MonoBehaviour
{
    public Rigidbody2D Physic;
    public WeaponInfo GunInHands;
    public float MoveSpeed;
    private void FixedUpdate()
    {
        Vector2 movement = new Vector2(Input.GetAxis("Horizontal"), Input.GetAxis("Vertical"));
        Physic.linearVelocity = movement * MoveSpeed;
    }
    private void Update()
    {
        if (Input.GetMouseButton(0))
        {
            GunInHands.Attack();
        }

        Vector3 mousePos = Camera.main.ScreenToWorldPoint(Input.mousePosition);
        Vector3 lookDirection = mousePos - transform.position;
        lookDirection.Normalize();
        float angle = Mathf.Atan2(lookDirection.y, lookDirection.x) * Mathf.Rad2Deg;
        transform.rotation = Quaternion.Euler(0, 0, angle);
    }
}
