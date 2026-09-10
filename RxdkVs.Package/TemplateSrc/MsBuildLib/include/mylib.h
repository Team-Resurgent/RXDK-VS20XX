//------------------------------------------------------------------------------
// mylib.h — public interface for this static library.
//
// Headers under a library's publicIncludePaths are exported to any project that
// references the library (via projectReferences), so consumers can #include them
// directly. Keep this header self-contained (no private implementation details).
//------------------------------------------------------------------------------
#pragma once

// Clamp v to the inclusive range [lo, hi].
float ClampF(float v, float lo, float hi);

// Linear interpolation between a and b by t (t in [0, 1]).
float LerpF(float a, float b, float t);
