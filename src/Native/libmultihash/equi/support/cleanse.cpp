// Copyright (c) 2009-2010 Satoshi Nakamoto
// Copyright (c) 2009-2015 The Bitcoin Core developers
// Distributed under the MIT software license, see the accompanying
// file COPYING or http://www.opensource.org/licenses/mit-license.php.

#include "cleanse.h"

#ifdef _WIN32
#include <Windows.h>
#else
#include <cstring>
#endif

void memory_cleanse(void *ptr, size_t len)
{
#ifdef _WIN32
    SecureZeroMemory(ptr, len);
#else
    static void *(*const volatile memset_secure)(void *, int, size_t) = &std::memset;
    memset_secure(ptr, 0, len);
#endif
}
