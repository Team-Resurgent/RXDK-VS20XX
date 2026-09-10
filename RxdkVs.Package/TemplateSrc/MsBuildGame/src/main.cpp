//------------------------------------------------------------------------------
// File: main.cpp
//
// Direct3D 8 rotating triangle — reference title sample for RXDK D3D8/D3DX8.
//
// Creates a HAL device at 640x480, draws a colored triangle with a Z-buffer,
// and rotates it using the world transform. Debug output uses printf (CRT
// routes stdout to the kit debug channel).
//------------------------------------------------------------------------------

#include <xtl.h>
#include <stdio.h>
#include <d3d8.h>
#include <d3dx8math.h>


namespace {

struct CustomVertex
{
    FLOAT x, y, z;
    DWORD color;
};

constexpr DWORD kCustomVertexFvf = D3DFVF_XYZ | D3DFVF_DIFFUSE;

LPDIRECT3D8 g_pD3D = NULL;
LPDIRECT3DDEVICE8 g_pd3dDevice = NULL;
LPDIRECT3DVERTEXBUFFER8 g_pVB = NULL;

LARGE_INTEGER g_qwLastTime = {};
LARGE_INTEGER g_qwAppTime = {};
LARGE_INTEGER g_qwTicksPerSec = {};
FLOAT g_secsPerTick = 0.0f;

void Cleanup()
{
    if (g_pVB) {
        g_pVB->Release();
        g_pVB = NULL;
    }
    if (g_pd3dDevice) {
        g_pd3dDevice->Release();
        g_pd3dDevice = NULL;
    }
    if (g_pD3D) {
        g_pD3D->Release();
        g_pD3D = NULL;
    }
}

void InitTime()
{
    QueryPerformanceFrequency(&g_qwTicksPerSec);
    QueryPerformanceCounter(&g_qwLastTime);
    g_secsPerTick = 1.0f / (FLOAT)(LONGLONG)g_qwTicksPerSec.QuadPart;
    g_qwAppTime.QuadPart = 0;
}

void UpdateTime()
{
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    g_qwAppTime.QuadPart += now.QuadPart - g_qwLastTime.QuadPart;
    g_qwLastTime = now;
}

HRESULT InitDevice()
{
    D3DPRESENT_PARAMETERS d3dpp;
    HRESULT hr;

    printf("Creating Direct3D 8 interface\n");
    g_pD3D = Direct3DCreate8(D3D_SDK_VERSION);
    if (!g_pD3D) {
        printf("Failed to create Direct3D 8 interface\n");
        return E_FAIL;
    }

    ZeroMemory(&d3dpp, sizeof(d3dpp));
    d3dpp.BackBufferWidth = 640;
    d3dpp.BackBufferHeight = 480;
    d3dpp.BackBufferFormat = D3DFMT_X8R8G8B8;
    d3dpp.BackBufferCount = 1;
    d3dpp.Windowed = FALSE;
    d3dpp.EnableAutoDepthStencil = TRUE;
    d3dpp.AutoDepthStencilFormat = D3DFMT_D24S8;
    d3dpp.SwapEffect = D3DSWAPEFFECT_DISCARD;
    d3dpp.FullScreen_RefreshRateInHz = 60;
    d3dpp.FullScreen_PresentationInterval = D3DPRESENT_INTERVAL_DEFAULT;

    Direct3D_SetPushBufferSize(512 * 1024, (512 * 1024) / 16);

    printf("Creating HAL device (640x480, Z-buffer)\n");
    hr = Direct3D_CreateDevice(
        D3DADAPTER_DEFAULT,
        D3DDEVTYPE_HAL,
        NULL,
        D3DCREATE_HARDWARE_VERTEXPROCESSING,
        &d3dpp,
        &g_pd3dDevice);
    if (FAILED(hr)) {
        return hr;
    }

    D3DVIEWPORT8 vp = {};
    vp.Width = d3dpp.BackBufferWidth;
    vp.Height = d3dpp.BackBufferHeight;
    vp.MinZ = 0.0f;
    vp.MaxZ = 1.0f;
    hr = g_pd3dDevice->SetViewport(&vp);
    if (FAILED(hr)) {
        return hr;
    }

    printf("Device created\n");
    return S_OK;
}

HRESULT InitTransforms()
{
    D3DXMATRIX matProj;
    D3DXMATRIX matView;
    D3DXMATRIX matWorld;
    HRESULT hr;

    D3DXMatrixPerspectiveFovLH(&matProj, D3DX_PI / 4.0f, 4.0f / 3.0f, 1.0f, 200.0f);
    hr = g_pd3dDevice->SetTransform(D3DTS_PROJECTION, &matProj);
    if (FAILED(hr)) {
        return hr;
    }

    {
        const D3DXVECTOR3 eye(0.0f, 0.0f, -7.0f);
        const D3DXVECTOR3 at(0.0f, 0.0f, 0.0f);
        const D3DXVECTOR3 up(0.0f, 1.0f, 0.0f);
        D3DXMatrixLookAtLH(&matView, &eye, &at, &up);
    }
    hr = g_pd3dDevice->SetTransform(D3DTS_VIEW, &matView);
    if (FAILED(hr)) {
        return hr;
    }

    D3DXMatrixIdentity(&matWorld);
    hr = g_pd3dDevice->SetTransform(D3DTS_WORLD, &matWorld);
    if (FAILED(hr)) {
        return hr;
    }

    printf("View and projection configured\n");
    return S_OK;
}

HRESULT InitGeometry()
{
    static const CustomVertex vertices[] = {
        {  0.0f, -1.1547f, 0.0f, 0xffffff00 },
        { -1.0f,  0.5777f, 0.0f, 0xff00ff00 },
        {  1.0f,  0.5777f, 0.0f, 0xffff0000 },
    };

    HRESULT hr = g_pd3dDevice->CreateVertexBuffer(
        3 * sizeof(CustomVertex),
        D3DUSAGE_WRITEONLY,
        kCustomVertexFvf,
        D3DPOOL_MANAGED,
        &g_pVB);
    if (FAILED(hr)) {
        return hr;
    }

    CustomVertex* pVertices = NULL;
    hr = g_pVB->Lock(0, 0, (BYTE**)&pVertices, 0);
    if (FAILED(hr)) {
        return hr;
    }
    memcpy(pVertices, vertices, sizeof(vertices));
    g_pVB->Unlock();

    printf("Vertex buffer created\n");
    return S_OK;
}

void UpdateScene()
{
    D3DXMATRIX matWorld;
    const FLOAT secs = (FLOAT)(LONGLONG)g_qwAppTime.QuadPart * g_secsPerTick;
    D3DXMatrixRotationZ(&matWorld, -secs * D3DX_PI * 0.5f);
    g_pd3dDevice->SetTransform(D3DTS_WORLD, &matWorld);
}

void RenderScene()
{
    g_pd3dDevice->Clear(
        0,
        NULL,
        D3DCLEAR_TARGET | D3DCLEAR_ZBUFFER,
        D3DCOLOR_XRGB(0, 0, 255),
        1.0f,
        0);

    g_pd3dDevice->BeginScene();
    g_pd3dDevice->SetRenderState(D3DRS_ZENABLE, TRUE);
    g_pd3dDevice->SetRenderState(D3DRS_LIGHTING, FALSE);
    g_pd3dDevice->SetStreamSource(0, g_pVB, sizeof(CustomVertex));
    g_pd3dDevice->SetVertexShader(kCustomVertexFvf);
    g_pd3dDevice->DrawPrimitive(D3DPT_TRIANGLELIST, 0, 1);
    g_pd3dDevice->EndScene();
}

} // namespace

int main()
{
    HRESULT hr;

    printf("Starting sample\n");

    hr = InitDevice();
    if (FAILED(hr)) {
        printf("Failed to initialize device (hr=0x%08X)\n", (unsigned)hr);
        Cleanup();
        return 1;
    }

    hr = InitGeometry();
    if (FAILED(hr)) {
        printf("Failed to create geometry (hr=0x%08X)\n", (unsigned)hr);
        Cleanup();
        return 1;
    }

    hr = InitTransforms();
    if (FAILED(hr)) {
        printf("Failed to configure transforms (hr=0x%08X)\n", (unsigned)hr);
        Cleanup();
        return 1;
    }

    InitTime();

    printf("Entering render loop\n");
    while (true) {
        UpdateTime();
        UpdateScene();
        RenderScene();
        g_pd3dDevice->Present(NULL, NULL, NULL, NULL);
    }

    return 0;
}
