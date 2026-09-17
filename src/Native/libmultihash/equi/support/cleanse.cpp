// Copyright (c) 2009-2010 Satoshi Nakamoto
// Copyright (c) 2009-2015 The Bitcoin Core developers
// Distributed under the MIT software license, see the accompanying
// file COPYING or http://www.opensource.org/licenses/mit-license.php.

#include "cleanse.h"

#ifdef _WIN32
#include <Windows.h>
#endif

void memory_cleanse(void *ptr, size_t len)
{
#ifdef _WIN32
    SecureZeroMemory(ptr, len);
#else
    volatile unsigned char *bytes = static_cast<volatile unsigned char *>(ptr);

    while(len-- > 0)
        *bytes++ = 0;
#endif
}
