#define A_GPU 1
#define A_HLSL 1
#define FSR_EASU_F 1
#include "ffx_a.h"
#include "ffx_fsr1.h"

Texture2D  texColor : register(t0);
SamplerState linClamp : register(s0);

cbuffer CB : register(b0)
{
    uint4 Con0;
    uint4 Con1;
    uint4 Con2;
    uint4 Con3;
};

AF4 FsrEasuRF(AF2 p) { return texColor.GatherRed(linClamp, p); }
AF4 FsrEasuGF(AF2 p) { return texColor.GatherGreen(linClamp, p); }
AF4 FsrEasuBF(AF2 p) { return texColor.GatherBlue(linClamp, p); }

struct VSOut
{
    float4 pos : SV_Position;
};

float4 main(VSOut i) : SV_Target
{
    AU2 gxy = AU2(AU1(i.pos.x), AU1(i.pos.y));
    AF3 pix;
    FsrEasuF(pix, gxy, Con0, Con1, Con2, Con3);
    return float4(pix, 1.0);
}
