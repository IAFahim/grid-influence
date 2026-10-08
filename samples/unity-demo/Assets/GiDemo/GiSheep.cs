using Gi;
using UnityEngine;

namespace GiDemo;

internal sealed class GiSheep
{
    public Transform Body;
    public Vector2 Pos;
    public Vector2 Vel;
    public float Phase;

    public void Update(float t)
    {
        var world = (byte)GiEcosystemDemo.WorldId;
        var grid = GiEcosystemDemo.GridOne;
        var threatLayer = GiEcosystemDemo.Threat;
        var foodLayer = GiEcosystemDemo.Food;
        var cx = (int)Pos.x;
        var cy = (int)Pos.y;

        var threat = GiEcosystemDemo.Q(world, grid, threatLayer, cx, cy);
        var food = GiEcosystemDemo.Q(world, grid, foodLayer, cx, cy);

        var escapeX = 0f;
        var escapeY = 0f;
        if (threat > 60)
        {
            var right = GiEcosystemDemo.Q(world, grid, threatLayer, cx + 3, cy);
            var left = GiEcosystemDemo.Q(world, grid, threatLayer, cx - 3, cy);
            var up = GiEcosystemDemo.Q(world, grid, threatLayer, cx, cy + 3);
            var down = GiEcosystemDemo.Q(world, grid, threatLayer, cx, cy - 3);
            escapeX = -(right - left) * 0.045f;
            escapeY = -(up - down) * 0.045f;
        }

        var seekX = 0f;
        var seekY = 0f;
        if (food > 40 && threat < 400)
        {
            var right = GiEcosystemDemo.Q(world, grid, foodLayer, cx + 3, cy);
            var left = GiEcosystemDemo.Q(world, grid, foodLayer, cx - 3, cy);
            var up = GiEcosystemDemo.Q(world, grid, foodLayer, cx, cy + 3);
            var down = GiEcosystemDemo.Q(world, grid, foodLayer, cx, cy - 3);
            seekX = (right - left) * 0.012f;
            seekY = (up - down) * 0.012f;
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

        Body.position = new Vector3(Pos.x, 0.5f, Pos.y);
        if (Vel.sqrMagnitude > 0.02f)
            Body.rotation = Quaternion.LookRotation(new Vector3(Vel.x, 0f, Vel.y), Vector3.up);
    }

    public void Respawn(System.Random rng)
    {
        Pos = new Vector2(8f + (float)rng.NextDouble() * 240f, 8f + (float)rng.NextDouble() * 240f);
        Vel = Vector2.zero;
    }
}
