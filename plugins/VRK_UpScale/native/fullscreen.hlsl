struct VSOut
{
    float4 pos : SV_Position;
};

VSOut main(uint id : SV_VertexID)
{
    VSOut o;
    // Fullscreen triangle: (0,0) (2,0) (0,2) in UV space, mapped to clip space.
    float2 uv = float2((float)(id << 1 & 2), (float)(id & 2));
    o.pos = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return o;
}
