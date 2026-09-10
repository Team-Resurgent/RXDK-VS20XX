//------------------------------------------------------------------------------
// mylib.cpp — implementation for this static library.
//
// This project builds to out/<name>.lib (type: "library"); it is not deployed or
// run on its own. Reference it from an executable project with:
//   "projectReferences": ["../my-lib"]
// and #include "mylib.h" (exported via publicIncludePaths).
//------------------------------------------------------------------------------
#include "mylib.h"

float ClampF(float v, float lo, float hi)
{
    if (v < lo) {
        return lo;
    }
    if (v > hi) {
        return hi;
    }
    return v;
}

float LerpF(float a, float b, float t)
{
    return a + (b - a) * t;
}
