using UnityEngine;

namespace GiDemo
{

internal sealed class GiWolf
{
    public Transform Body;
    public Vector2 Pos;
    public Vector2 Vel;
    public int Source;
    public int Target = -1;
    public float RetryClock;
    public int Gain = 7;

    public void Sync()
    {
        Body.position = new Vector3(Pos.x, 0.9f, Pos.y);
        if (Vel.sqrMagnitude > 0.02f)
            Body.rotation = Quaternion.LookRotation(new Vector3(Vel.x, 0f, Vel.y), Vector3.up);
    }
}
}
