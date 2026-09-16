#pragma once
#include "sigscan.h"

struct FlScanContext;

// Finds a unique Delphi metaclass by short-string name and validates its VMT structure.
// pointerBase is the base encoded in image pointers: zero selects the actual live image base;
// a private, unrelocated PE diagnostic buffer must pass the PE preferred image base instead.
// A valid metaclass does not establish compatibility of instance fields or UI event layouts.
ResolveStatus resolveDelphiClassRef(const FlScanContext& context, const char* className,
                                   uint64_t* outAddress, uint64_t pointerBase = 0);
