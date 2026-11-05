// vrkup.cpp - Unity D3D11 native upscaler for VR_Kanojo (FSR1 EASU + RCAS).
//
// Why a native plugin: Unity 5.4 cannot compile custom shaders at runtime and
// exposes no shader hook, so the upscale passes run directly on Unity's own
// D3D11 device. The device is obtained from the texture Unity hands us
// (ID3D11Resource::GetDevice), so no proxy DLL, no exe patching and no
// dependency on the deprecated UnitySetGraphicsDevice callback is required.
//
// Shader bytecode is precompiled with fxc (ps_5_0 / vs_5_0) and embedded, so
// this DLL has no runtime dependency on d3dcompiler.dll.
//
// Every failure is reported as a negative return code; the managed side then
// disables itself and the game renders exactly as before.

#include <windows.h>
#include <d3d11.h>
#include <stdio.h>
#include <string.h>
#include <math.h>
#include "shader_blobs.h"

#define VRKU_VERSION 10203

static ID3D11Device *g_dev = nullptr;
static ID3D11DeviceContext *g_ctx = nullptr;
static char g_err[512] = {0};
static int g_status = 0; // 0 = not built yet, 1 = ready, <0 = failed

static ID3D11VertexShader *g_vsShader = nullptr;
static ID3D11PixelShader *g_psEasuShader = nullptr;
static ID3D11PixelShader *g_psRcasShader = nullptr;
static ID3D11SamplerState *g_smp = nullptr;
static ID3D11RasterizerState *g_rsSolid = nullptr;
static ID3D11Buffer *g_cb = nullptr;
static ID3D11Texture2D *g_temp = nullptr;
static ID3D11RenderTargetView *g_tempRTV = nullptr;
static ID3D11ShaderResourceView *g_tempSRV = nullptr;

static int g_srcW = 0, g_srcH = 0, g_dstW = 0, g_dstH = 0;
static float g_sharpness = 0.2f;
static DXGI_FORMAT g_fmt = DXGI_FORMAT_UNKNOWN;
static unsigned int g_easuCon[16];
static unsigned int g_rcasCon[4];
static bool g_pipelineReady = false;
static unsigned int g_frames = 0;
static unsigned int g_failFrames = 0;

static void SetErr(const char *what, HRESULT hr)
{
    sprintf_s(g_err, sizeof(g_err), "%s (hr=0x%08lx)", what, (unsigned long)hr);
    g_status = -1;
}

struct ViewCache
{
    ID3D11Resource *res;
    ID3D11ShaderResourceView *srv;
    ID3D11RenderTargetView *rtv;
};
static ViewCache g_cache[8];
// Format of the texture Unity wants the finished frame in (the camera target).
static DXGI_FORMAT g_dstFmt = DXGI_FORMAT_UNKNOWN;

static DXGI_FORMAT TypedFormat(DXGI_FORMAT f)
{
    switch (f)
    {
    case DXGI_FORMAT_R8G8B8A8_TYPELESS:
        return DXGI_FORMAT_R8G8B8A8_UNORM;
    case DXGI_FORMAT_B8G8R8A8_TYPELESS:
        return DXGI_FORMAT_B8G8R8A8_UNORM;
    case DXGI_FORMAT_R10G10B10A2_TYPELESS:
        return DXGI_FORMAT_R10G10B10A2_UNORM;
    case DXGI_FORMAT_R16G16B16A16_TYPELESS:
        return DXGI_FORMAT_R16G16B16A16_FLOAT;
    case DXGI_FORMAT_R32G32B32A32_TYPELESS:
        return DXGI_FORMAT_R32G32B32A32_FLOAT;
    default:
        return f;
    }
}

static bool ViewableFormat(DXGI_FORMAT f)
{
    switch (f)
    {
    case DXGI_FORMAT_UNKNOWN:
    case DXGI_FORMAT_R24G8_TYPELESS:
    case DXGI_FORMAT_D24_UNORM_S8_UINT:
    case DXGI_FORMAT_D32_FLOAT:
    case DXGI_FORMAT_D16_UNORM:
    case DXGI_FORMAT_R32_TYPELESS:
    case DXGI_FORMAT_NV12:
        return false;
    default:
        return true;
    }
}

static void ClearViewCache()
{
    for (int i = 0; i < 8; ++i)
    {
        if (g_cache[i].srv)
            g_cache[i].srv->Release();
        if (g_cache[i].rtv)
            g_cache[i].rtv->Release();
        g_cache[i].res = nullptr;
        g_cache[i].srv = nullptr;
        g_cache[i].rtv = nullptr;
    }
}

static ViewCache *CacheFor(ID3D11Resource *res)
{
    for (int i = 0; i < 8; ++i)
        if (g_cache[i].res == res)
            return &g_cache[i];
    for (int i = 0; i < 8; ++i)
        if (g_cache[i].res == nullptr)
        {
            g_cache[i].res = res;
            g_cache[i].srv = nullptr;
            g_cache[i].rtv = nullptr;
            return &g_cache[i];
        }
    if (g_cache[0].srv)
        g_cache[0].srv->Release();
    if (g_cache[0].rtv)
        g_cache[0].rtv->Release();
    g_cache[0].res = res;
    g_cache[0].srv = nullptr;
    g_cache[0].rtv = nullptr;
    return &g_cache[0];
}

static ID3D11ShaderResourceView *GetSrv(ID3D11Resource *res, DXGI_FORMAT fmt)
{
    ViewCache *c = CacheFor(res);
    if (c->srv)
        return c->srv;
    D3D11_SHADER_RESOURCE_VIEW_DESC sd;
    memset(&sd, 0, sizeof(sd));
    sd.Format = TypedFormat(fmt);
    sd.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    sd.Texture2D.MipLevels = 1;
    if (FAILED(g_dev->CreateShaderResourceView(res, &sd, &c->srv)))
    {
        SetErr("CreateShaderResourceView", E_FAIL);
        return nullptr;
    }
    return c->srv;
}

static ID3D11RenderTargetView *GetRtv(ID3D11Resource *res, DXGI_FORMAT fmt)
{
    ViewCache *c = CacheFor(res);
    if (c->rtv)
        return c->rtv;
    D3D11_RENDER_TARGET_VIEW_DESC rd;
    memset(&rd, 0, sizeof(rd));
    rd.Format = TypedFormat(fmt);
    rd.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
    if (FAILED(g_dev->CreateRenderTargetView(res, &rd, &c->rtv)))
    {
        SetErr("CreateRenderTargetView", E_FAIL);
        return nullptr;
    }
    return c->rtv;
}

// -------------------------------------------------------------------------
// FSR constant setup (ported from the host-side helpers in ffx_fsr1.h)
// -------------------------------------------------------------------------
static inline unsigned int AsUint(float f)
{
    unsigned int u;
    memcpy(&u, &f, 4);
    return u;
}

static void FsrEasuConHost(unsigned int c0[4], unsigned int c1[4], unsigned int c2[4], unsigned int c3[4],
                           float ivw, float ivh, float isw, float ish, float ow, float oh)
{
    float rcpOw = 1.0f / ow, rcpOh = 1.0f / oh, rcpIsw = 1.0f / isw, rcpIsh = 1.0f / ish;
    c0[0] = AsUint(ivw * rcpOw);
    c0[1] = AsUint(ivh * rcpOh);
    c0[2] = AsUint(0.5f * ivw * rcpOw - 0.5f);
    c0[3] = AsUint(0.5f * ivh * rcpOh - 0.5f);
    c1[0] = AsUint(rcpIsw);
    c1[1] = AsUint(rcpIsh);
    c1[2] = AsUint(1.0f * rcpIsw);
    c1[3] = AsUint(-1.0f * rcpIsh);
    c2[0] = AsUint(-1.0f * rcpIsw);
    c2[1] = AsUint(2.0f * rcpIsh);
    c2[2] = AsUint(1.0f * rcpIsw);
    c2[3] = AsUint(2.0f * rcpIsh);
    c3[0] = AsUint(0.0f * rcpIsw);
    c3[1] = AsUint(4.0f * rcpIsh);
    c3[2] = 0;
    c3[3] = 0;
}

static void FsrRcasConHost(unsigned int c[4], float sharpness)
{
    float s = powf(2.0f, -sharpness);
    c[0] = AsUint(s);
    c[1] = 0;
    c[2] = 0;
    c[3] = 0;
}

// -------------------------------------------------------------------------
// D3D11 state save/restore so Unity's frame is left untouched
// -------------------------------------------------------------------------
struct SavedState
{
    ID3D11RenderTargetView *rtv;
    ID3D11DepthStencilView *dsv;
    D3D11_VIEWPORT vp;
    UINT vpCount;
    ID3D11InputLayout *layout;
    D3D11_PRIMITIVE_TOPOLOGY topo;
    ID3D11VertexShader *vs;
    ID3D11PixelShader *ps;
    ID3D11SamplerState *smp;
    ID3D11ShaderResourceView *srv;
    ID3D11Buffer *cb;
    ID3D11RasterizerState *rs;
    ID3D11BlendState *bs;
    ID3D11DepthStencilState *ds;
    FLOAT blendFactor[4];
    UINT sampleMask;
    UINT stencilRef;
};

static void SaveState(SavedState *s)
{
    memset(s, 0, sizeof(*s));
    g_ctx->OMGetRenderTargets(1, &s->rtv, &s->dsv);
    s->vpCount = 1;
    g_ctx->RSGetViewports(&s->vpCount, &s->vp);
    g_ctx->IAGetInputLayout(&s->layout);
    g_ctx->IAGetPrimitiveTopology(&s->topo);
    g_ctx->VSGetShader(&s->vs, nullptr, nullptr);
    g_ctx->PSGetShader(&s->ps, nullptr, nullptr);
    g_ctx->PSGetSamplers(0, 1, &s->smp);
    g_ctx->PSGetShaderResources(0, 1, &s->srv);
    g_ctx->PSGetConstantBuffers(0, 1, &s->cb);
    g_ctx->RSGetState(&s->rs);
    g_ctx->OMGetBlendState(&s->bs, s->blendFactor, &s->sampleMask);
    g_ctx->OMGetDepthStencilState(&s->ds, &s->stencilRef);
}

static void RestoreState(SavedState *s)
{
    g_ctx->OMSetRenderTargets(1, &s->rtv, s->dsv);
    if (s->vpCount)
        g_ctx->RSSetViewports(s->vpCount, &s->vp);
    g_ctx->IASetInputLayout(s->layout);
    g_ctx->IASetPrimitiveTopology(s->topo);
    g_ctx->VSSetShader(s->vs, nullptr, 0);
    g_ctx->PSSetShader(s->ps, nullptr, 0);
    g_ctx->PSSetSamplers(0, 1, &s->smp);
    g_ctx->PSSetShaderResources(0, 1, &s->srv);
    g_ctx->PSSetConstantBuffers(0, 1, &s->cb);
    g_ctx->RSSetState(s->rs);
    g_ctx->OMSetBlendState(s->bs, s->blendFactor, s->sampleMask);
    g_ctx->OMSetDepthStencilState(s->ds, s->stencilRef);

    if (s->rtv) s->rtv->Release();
    if (s->dsv) s->dsv->Release();
    if (s->layout) s->layout->Release();
    if (s->vs) s->vs->Release();
    if (s->ps) s->ps->Release();
    if (s->smp) s->smp->Release();
    if (s->srv) s->srv->Release();
    if (s->cb) s->cb->Release();
    if (s->rs) s->rs->Release();
    if (s->bs) s->bs->Release();
    if (s->ds) s->ds->Release();
}

// -------------------------------------------------------------------------
static void ReleasePipeline()
{
    if (g_tempSRV) { g_tempSRV->Release(); g_tempSRV = nullptr; }
    if (g_tempRTV) { g_tempRTV->Release(); g_tempRTV = nullptr; }
    if (g_temp) { g_temp->Release(); g_temp = nullptr; }
    if (g_cb) { g_cb->Release(); g_cb = nullptr; }
    if (g_smp) { g_smp->Release(); g_smp = nullptr; }
    if (g_rsSolid) { g_rsSolid->Release(); g_rsSolid = nullptr; }
    if (g_psRcasShader) { g_psRcasShader->Release(); g_psRcasShader = nullptr; }
    if (g_psEasuShader) { g_psEasuShader->Release(); g_psEasuShader = nullptr; }
    if (g_vsShader) { g_vsShader->Release(); g_vsShader = nullptr; }
    ClearViewCache();
    g_pipelineReady = false;
}

static bool CreateShadersAndStates()
{
    HRESULT hr = g_dev->CreateVertexShader(kVbytecode, kVbytecode_len, nullptr, &g_vsShader);
    if (FAILED(hr)) { SetErr("CreateVertexShader", hr); return false; }
    hr = g_dev->CreatePixelShader(kEasuBytecode, kEasuBytecode_len, nullptr, &g_psEasuShader);
    if (FAILED(hr)) { SetErr("CreatePixelShader(easu)", hr); return false; }
    hr = g_dev->CreatePixelShader(kRcasBytecode, kRcasBytecode_len, nullptr, &g_psRcasShader);
    if (FAILED(hr)) { SetErr("CreatePixelShader(rcas)", hr); return false; }

    D3D11_SAMPLER_DESC sd;
    memset(&sd, 0, sizeof(sd));
    sd.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    sd.ComparisonFunc = D3D11_COMPARISON_NEVER;
    sd.MaxLOD = D3D11_FLOAT32_MAX;
    hr = g_dev->CreateSamplerState(&sd, &g_smp);
    if (FAILED(hr)) { SetErr("CreateSamplerState", hr); return false; }

    D3D11_BUFFER_DESC bd;
    memset(&bd, 0, sizeof(bd));
    bd.ByteWidth = 64;
    bd.Usage = D3D11_USAGE_DYNAMIC;
    bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    hr = g_dev->CreateBuffer(&bd, nullptr, &g_cb);
    if (FAILED(hr)) { SetErr("CreateBuffer(cb)", hr); return false; }

    // Never inherit Unity's cull mode: our fullscreen triangle must always survive.
    D3D11_RASTERIZER_DESC rd;
    memset(&rd, 0, sizeof(rd));
    rd.FillMode = D3D11_FILL_SOLID;
    rd.CullMode = D3D11_CULL_NONE;
    rd.DepthClipEnable = TRUE;
    rd.ScissorEnable = FALSE;
    rd.MultisampleEnable = FALSE;
    rd.AntialiasedLineEnable = FALSE;
    hr = g_dev->CreateRasterizerState(&rd, &g_rsSolid);
    if (FAILED(hr)) { SetErr("CreateRasterizerState", hr); return false; }
    return true;
}

static bool CreateTemp(int w, int h, DXGI_FORMAT fmt)
{
    if (g_tempSRV) { g_tempSRV->Release(); g_tempSRV = nullptr; }
    if (g_tempRTV) { g_tempRTV->Release(); g_tempRTV = nullptr; }
    if (g_temp) { g_temp->Release(); g_temp = nullptr; }

    D3D11_TEXTURE2D_DESC td;
    memset(&td, 0, sizeof(td));
    td.Width = (UINT)w;
    td.Height = (UINT)h;
    td.MipLevels = 1;
    td.ArraySize = 1;
    td.Format = fmt;
    td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_DEFAULT;
    td.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    HRESULT hr = g_dev->CreateTexture2D(&td, nullptr, &g_temp);
    if (FAILED(hr)) { SetErr("CreateTexture2D(temp)", hr); return false; }

    D3D11_RENDER_TARGET_VIEW_DESC rd;
    memset(&rd, 0, sizeof(rd));
    rd.Format = TypedFormat(fmt);
    rd.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
    hr = g_dev->CreateRenderTargetView(g_temp, &rd, &g_tempRTV);
    if (FAILED(hr)) { SetErr("CreateRenderTargetView(temp)", hr); return false; }

    D3D11_SHADER_RESOURCE_VIEW_DESC sd;
    memset(&sd, 0, sizeof(sd));
    sd.Format = TypedFormat(fmt);
    sd.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    sd.Texture2D.MipLevels = 1;
    hr = g_dev->CreateShaderResourceView(g_temp, &sd, &g_tempSRV);
    if (FAILED(hr)) { SetErr("CreateShaderResourceView(temp)", hr); return false; }
    return true;
}

// Acquire the device lazily from any D3D11 resource Unity gives us, so the
// plugin never depends on deprecated callbacks.
static bool AcquireDeviceFrom(ID3D11Resource *probe)
{
    if (g_dev && g_ctx)
        return true;
    if (!probe)
        return false;
    ID3D11Device *dev = nullptr;
    probe->GetDevice(&dev); // D3D11 COM signature returns void
    if (!dev)
    {
        strcpy_s(g_err, sizeof(g_err), "ID3D11Resource::GetDevice failed");
        g_status = -2;
        return false;
    }
    ID3D11DeviceContext *ctx = nullptr;
    dev->GetImmediateContext(&ctx);
    if (!ctx)
    {
        dev->Release();
        strcpy_s(g_err, sizeof(g_err), "GetImmediateContext failed");
        g_status = -2;
        return false;
    }
    if (g_dev)
        g_dev->Release();
    if (g_ctx)
        g_ctx->Release();
    g_dev = dev;
    g_ctx = ctx;
    g_status = 1;
    return true;
}

static bool EnsurePipeline(ID3D11Resource *probe, DXGI_FORMAT srcFmt)
{
    if (g_pipelineReady)
        return true;
    if (!AcquireDeviceFrom(probe))
        return false;
    g_fmt = srcFmt;
    if (!CreateShadersAndStates())
        return false;
    FsrEasuConHost(g_easuCon + 0, g_easuCon + 4, g_easuCon + 8, g_easuCon + 12,
                   (float)g_srcW, (float)g_srcH, (float)g_srcW, (float)g_srcH,
                   (float)g_dstW, (float)g_dstH);
    FsrRcasConHost(g_rcasCon, g_sharpness);
    if (!CreateTemp(g_dstW, g_dstH, (g_dstFmt != DXGI_FORMAT_UNKNOWN) ? g_dstFmt : g_fmt))
        return false;
    g_pipelineReady = true;
    g_status = 1;
    g_err[0] = 0;
    return true;
}

static void UpdateCb(const unsigned int *data, int countUints)
{
    D3D11_MAPPED_SUBRESOURCE m;
    if (FAILED(g_ctx->Map(g_cb, 0, D3D11_MAP_WRITE_DISCARD, 0, &m)))
        return;
    memset(m.pData, 0, 64);
    memcpy(m.pData, data, (size_t)countUints * 4);
    g_ctx->Unmap(g_cb, 0);
}

static void DrawFullscreen()
{
    g_ctx->IASetInputLayout(nullptr);
    if (g_rsSolid) g_ctx->RSSetState(g_rsSolid);
    g_ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    g_ctx->VSSetShader(g_vsShader, nullptr, 0);
    g_ctx->PSSetSamplers(0, 1, &g_smp);
    g_ctx->PSSetConstantBuffers(0, 1, &g_cb);
    g_ctx->Draw(3, 0);
}

extern "C"
{
    __declspec(dllexport) int __stdcall VRKU_Version(void)
    {
        return VRKU_VERSION;
    }

    __declspec(dllexport) const char *__stdcall VRKU_LastError(void)
    {
        return g_err;
    }

    __declspec(dllexport) int __stdcall VRKU_Status(void)
    {
        if (g_status == 1)
            return g_pipelineReady ? 1 : 0;
        return g_status;
    }

    __declspec(dllexport) unsigned int __stdcall VRKU_Frames(void)
    {
        return g_frames;
    }

    __declspec(dllexport) unsigned int __stdcall VRKU_FailFrames(void)
    {
        return g_failFrames;
    }

    __declspec(dllexport) const char *__stdcall VRKU_DeviceName(void)
    {
        static char name[256];
        name[0] = 0;
        if (!g_dev)
            return name;
        IDXGIDevice *dxdev = nullptr;
        if (FAILED(g_dev->QueryInterface(__uuidof(IDXGIDevice), (void **)&dxdev)))
            return name;
        IDXGIAdapter *ad = nullptr;
        if (SUCCEEDED(dxdev->GetAdapter(&ad)))
        {
            DXGI_ADAPTER_DESC d;
            if (SUCCEEDED(ad->GetDesc(&d)))
                WideCharToMultiByte(CP_UTF8, 0, d.Description, -1, name, sizeof(name), nullptr, nullptr);
            ad->Release();
        }
        dxdev->Release();
        return name;
    }

    // srcW/srcH = resolution actually rendered by the camera,
    // dstW/dstH = display resolution. src == dst selects RCAS-only (sharpen).
    __declspec(dllexport) int __stdcall VRKU_Init(int srcW, int srcH, int dstW, int dstH, float sharpness)
    {
        if (srcW < 8 || srcH < 8 || dstW < 8 || dstH < 8)
        {
            strcpy_s(g_err, sizeof(g_err), "invalid resolution");
            g_status = -3;
            return -3;
        }
        if (srcW > 16384 || srcH > 16384 || dstW > 16384 || dstH > 16384)
        {
            strcpy_s(g_err, sizeof(g_err), "resolution too large");
            g_status = -3;
            return -3;
        }
        if (sharpness < 0.0f)
            sharpness = 0.0f;
        if (sharpness > 1.0f)
            sharpness = 1.0f;

        if (g_pipelineReady && srcW == g_srcW && srcH == g_srcH && dstW == g_dstW &&
            dstH == g_dstH && sharpness == g_sharpness)
            return 0;

        ReleasePipeline();
        g_srcW = srcW;
        g_srcH = srcH;
        g_dstW = dstW;
        g_dstH = dstH;
        g_sharpness = sharpness;
        g_status = 0;
        return 0;
    }

    // srcTex / dstTex are ID3D11Texture2D* (RenderTexture.GetNativeTexturePtr()).
    __declspec(dllexport) int __stdcall VRKU_Run(void *srcTex, void *dstTex)
    {
        if (!srcTex || !dstTex)
            return -2;

        ID3D11Resource *src = (ID3D11Resource *)srcTex;
        ID3D11Resource *dst = (ID3D11Resource *)dstTex;

        ID3D11Texture2D *src2d = nullptr;
        if (FAILED(src->QueryInterface(__uuidof(ID3D11Texture2D), (void **)&src2d)))
        {
            strcpy_s(g_err, sizeof(g_err), "source is not a Texture2D");
            g_failFrames++;
            return -3;
        }
        D3D11_TEXTURE2D_DESC sd;
        src2d->GetDesc(&sd);
        src2d->Release();

        ID3D11Texture2D *dst2d = nullptr;
        if (SUCCEEDED(dst->QueryInterface(__uuidof(ID3D11Texture2D), (void **)&dst2d)))
        {
            D3D11_TEXTURE2D_DESC dd;
            dst2d->GetDesc(&dd);
            dst2d->Release();
            g_dstFmt = dd.Format;
        }

        DXGI_FORMAT fmt = TypedFormat(sd.Format);
        if (!ViewableFormat(fmt))
        {
            sprintf_s(g_err, sizeof(g_err), "unsupported source format %d", (int)sd.Format);
            g_failFrames++;
            return -4;
        }

        if (!EnsurePipeline(src, fmt))
        {
            g_failFrames++;
            return -5;
        }

        ID3D11ShaderResourceView *srcSrv = GetSrv(src, fmt);
        if (!srcSrv)
        {
            g_failFrames++;
            return -6;
        }

        SavedState st;
        SaveState(&st);

        D3D11_VIEWPORT vp;
        memset(&vp, 0, sizeof(vp));
        vp.Width = (float)g_dstW;
        vp.Height = (float)g_dstH;
        vp.MaxDepth = 1.0f;
        g_ctx->RSSetViewports(1, &vp);
        g_ctx->OMSetBlendState(nullptr, nullptr, 0xffffffff);
        g_ctx->OMSetDepthStencilState(nullptr, 0);

        if (src == dst)
        {
            // Unity handed us the same surface; sampling and writing it at once is undefined.
            RestoreState(&st);
            g_failFrames++;
            strcpy_s(g_err, sizeof(g_err), "src == dst");
            return -8;
        }

        bool sharpenOnly = (g_srcW == g_dstW && g_srcH == g_dstH);
        ID3D11ShaderResourceView *nullSrv = nullptr;

        // Always finish by writing Unity's own target; never CopyResource between
        // two different formats (that silently fails and leaves the screen empty).
        DXGI_FORMAT outFmt = (g_dstFmt != DXGI_FORMAT_UNKNOWN) ? TypedFormat(g_dstFmt) : fmt;
        ID3D11RenderTargetView *dstRtv = GetRtv(dst, outFmt);
        if (!dstRtv)
        {
            RestoreState(&st);
            g_failFrames++;
            return -7;
        }

        if (sharpenOnly)
        {
            g_ctx->OMSetRenderTargets(1, &dstRtv, nullptr);
            g_ctx->PSSetShaderResources(0, 1, &srcSrv);
            UpdateCb(g_rcasCon, 4);
            g_ctx->PSSetShader(g_psRcasShader, nullptr, 0);
            DrawFullscreen();
            g_ctx->PSSetShaderResources(0, 1, &nullSrv);
        }
        else
        {
            g_ctx->OMSetRenderTargets(1, &g_tempRTV, nullptr);
            g_ctx->PSSetShaderResources(0, 1, &srcSrv);
            UpdateCb(g_easuCon, 16);
            g_ctx->PSSetShader(g_psEasuShader, nullptr, 0);
            DrawFullscreen();
            g_ctx->PSSetShaderResources(0, 1, &nullSrv);

            g_ctx->OMSetRenderTargets(1, &dstRtv, nullptr);
            g_ctx->PSSetShaderResources(0, 1, &g_tempSRV);
            UpdateCb(g_rcasCon, 4);
            g_ctx->PSSetShader(g_psRcasShader, nullptr, 0);
            DrawFullscreen();
            g_ctx->PSSetShaderResources(0, 1, &nullSrv);
        }

        RestoreState(&st);
        g_frames++;
        return 0;
    }

    __declspec(dllexport) void __stdcall VRKU_Shutdown(void)
    {
        ReleasePipeline();
        if (g_ctx) { g_ctx->Release(); g_ctx = nullptr; }
        if (g_dev) { g_dev->Release(); g_dev = nullptr; }
        g_status = 0;
    }

    // ---- Unity plugin entry points --------------------------------------
    __declspec(dllexport) void __stdcall UnityPluginLoad(void *unityInterfaces)
    {
        (void)unityInterfaces;
    }

    __declspec(dllexport) void __stdcall UnityPluginUnload(void)
    {
        VRKU_Shutdown();
    }

    // Kept for early device capture on engines that still raise it.
    // deviceType 2 == kUnityGfxRendererD3D11, eventType 0 == initialize.
    __declspec(dllexport) void __stdcall UnitySetGraphicsDevice(void *device, int deviceType, int eventType)
    {
        if (deviceType == 2 && eventType == 0 && device && !g_dev)
        {
            ID3D11Device *dev = (ID3D11Device *)device;
            ID3D11DeviceContext *ctx = nullptr;
            dev->GetImmediateContext(&ctx);
            if (ctx)
            {
                dev->AddRef();
                g_dev = dev;
                g_ctx = ctx;
                if (g_status == 0)
                    g_status = 1;
            }
        }
    }
}
