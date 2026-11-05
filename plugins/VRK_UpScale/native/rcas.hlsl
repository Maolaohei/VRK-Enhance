#define A_GPU 1
#define A_HLSL 1
#define FSR_RCAS_F 1
#include "ffx_a.h"
#include "ffx_fsr1.h"

Texture2D  texColor : register(t0);
SamplerState linClamp : register(s0);

cbuffer CB : register(b0)
{
    uint4 Con0;
};

AF4 FsrRcasLoadF(ASU2 p) { return texColor.Load(int3(p, 0)); }
void FsrRcasInputF(inout AF1 r, inout AF1 g, inout AF1 b) {}

struct VSOut
{
    float4 pos : SV_Position;
};

float4 main(VSOut i) : SV_Target
{
    AU2 ip = AU2(AU1(i.pos.x), AU1(i.pos.y));
    AF1 r, g, b;
    FsrRcasF(r, g, b, ip, Con0);
    return float4(r, g, b, 1.0);
}
