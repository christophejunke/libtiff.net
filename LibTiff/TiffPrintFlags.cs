﻿/* Copyright (C) 2008-2009, Bit Miracle
 * http://www.bitmiracle.com
 * 
 * This software is based in part on the work of the Sam Leffler, Silicon 
 * Graphics, Inc. and contributors.
 *
 * Copyright (c) 1988-1997 Sam Leffler
 * Copyright (c) 1991-1997 Silicon Graphics, Inc.
 * For conditions of distribution and use, see the accompanying README file.
 */

using System;
using System.Collections.Generic;
using System.Text;

namespace BitMiracle.LibTiff.Classic
{
    /// <summary>
    /// Flags to pass to PrintDirectory to control printing of data structures 
    /// that are potentially very large. Bit-or these flags to enable printing
    /// multiple items.
    /// </summary>
    [Flags]
#if EXPOSE_LIBTIFF
    public
#endif
    enum TiffPrintFlags
    {
        /// <summary>
        /// no extra info
        /// </summary>
        NONE = 0x0,

        /// <summary>
        /// strips/tiles info
        /// </summary>
        STRIPS = 0x1,

        /// <summary>
        /// color/gray response curves
        /// </summary>
        CURVES = 0x2,

        /// <summary>
        /// colormap
        /// </summary>
        COLORMAP = 0x4,

        /// <summary>
        /// JPEG Q matrices
        /// </summary>
        JPEGQTABLES = 0x100,

        /// <summary>
        /// JPEG AC tables
        /// </summary>
        JPEGACTABLES = 0x200,

        /// <summary>
        /// JPEG DC tables
        /// </summary>
        JPEGDCTABLES = 0x200,
    }
}