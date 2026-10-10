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
        var world = (byte)GiEcosystemDemo.WorldId;
        var grid = GiEcosystemDemo.GridOne;
        var threatLayer = GiEcosystemDemo.Threat;
        var foodLayer = GiEcosystemDemo.Food;

        var threat = GiEcosystemDemo.Q(world, grid, threatLayer, (int)Pos.x, (int)Pos.y);
        var food = GiEcosystemDemo.Q(world, grid, foodLayer, (int)Pos.x, (int)Pos.y);

        var escapeX = 0f;
        var escapeY = 0f;
        if (threat > 60)
        {
            GiEcosystemDemo.G(world, grid, threatLayer, Pos.x, Pos.y, out var gx, out var gy);
            escapeX = -gx * 0.14f;
            escapeY = -gy * 0.14f;
        }

        var seekX = 0f;
        var seekY = 0f;
        if (food > 40 && threat < 400)
        {
            GiEcosystemDemo.G(world, grid, foodLayer, Pos.x, Pos.y, out var gx, out var gy);
            seekX = gx * 0.04f;
            seekY = gy * 0.04f;
        }

        var wanderX = Mathf.Sin(t * 0.7f + Phase) * 0.4f;
        var wanderY = Mathf.Cos(t * 0.6f + Phase * 1.3f) * 0.4f;
        var desired = new Vector2(escapeX + seekX + wanderX, escapeY + seekY + wanderY);
        if (desired.sqrMagnitude > 1f) desired.Normalize();

        var speed = threat > 900 ? 5.6f : 2.1f;
        if (food > 450 && threat < 60) speed *= 0.15f;

        Vel = Vector2.Lerp(Vel, desired * speed, 0.09f);
        Pos += Vel * Time.deltaTime;
        Pos = GiEcosystemDemo.ClampWorld(Pos);
        World.Move(world, Source, Pos.x, Pos.y);

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
