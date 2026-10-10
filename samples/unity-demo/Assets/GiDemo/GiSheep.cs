using Gi;
using UnityEngine;

namespace GiDemo
{

internal sealed class GiSheep
{
    public Transform Body;
    public Vector2 Pos;
    public Vector2 Vel;
    public float Phase;
    public int Source;

    public void Update(float t)
    {
        var threat = GiEcosystemDemo.Sense(GiEcosystemDemo.Threat, Pos);
        var food = GiEcosystemDemo.Sense(GiEcosystemDemo.Food, Pos);

        var escape = Vector2.zero;
        if (threat > 60) escape = -GiEcosystemDemo.Gradient(GiEcosystemDemo.Threat, Pos, -1) * 0.28f;

        var seek = Vector2.zero;
        if (food > 40 && threat < 400) seek = GiEcosystemDemo.Gradient(GiEcosystemDemo.Food, Pos, -1) * 0.08f;

        var space = Vector2.zero;
        if (GiEcosystemDemo.Crowd(Pos, Source) > GiEcosystemDemo.Crowded)
        {
            GiEcosystemDemo.CrowdedSheep++;
            space = -GiEcosystemDemo.Gradient(GiEcosystemDemo.Herd, Pos, Source) * 0.012f;
        }

        var wanderX = Mathf.Sin(t * 0.7f + Phase) * 0.4f;
        var wanderY = Mathf.Cos(t * 0.6f + Phase * 1.3f) * 0.4f;
        var desired = escape + seek + space + new Vector2(wanderX, wanderY);
        if (desired.sqrMagnitude > 1f) desired.Normalize();

        var speed = threat > 900 ? 5.6f : 2.1f;
        if (food > 450 && threat < 60) speed *= 0.15f;

        Vel = Vector2.Lerp(Vel, desired * speed, 0.09f);
        Pos += Vel * Time.deltaTime;
        Pos = GiEcosystemDemo.ClampWorld(Pos);
        World.Move((byte)GiEcosystemDemo.WorldId, Source, Pos.x, Pos.y);

        Body.position = new Vector3(Pos.x, 0.5f, Pos.y);
        if (Vel.sqrMagnitude > 0.02f)
            Body.rotation = Quaternion.LookRotation(new Vector3(Vel.x, 0f, Vel.y), Vector3.up);
    }

    public void Respawn(System.Random rng)
    {
        Pos = new Vector2(8f + (float)rng.NextDouble() * 240f, 8f + (float)rng.NextDouble() * 240f);
        Vel = Vector2.zero;
        World.Move((byte)GiEcosystemDemo.WorldId, Source, Pos.x, Pos.y);
    }
}
}
