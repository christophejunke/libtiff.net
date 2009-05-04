﻿/* Copyright (C) 2008-2009, Bit Miracle
 * http://www.bitmiracle.com
 * 
 * Copyright (C) 1994-1996, Thomas G. Lane.
 * This file is part of the Independent JPEG Group's software.
 * For conditions of distribution and use, see the accompanying README file.
 *
 */

/*
 * This file contains routines to decode JPEG datastream markers.
 * Most of the complexity arises from our desire to support input
 * suspension: if not all of the data for a marker is available,
 * we must exit back to the application.  On resumption, we reprocess
 * the marker.
 */

using System;
using System.Collections.Generic;
using System.Text;

namespace LibJpeg.NET
{
    /// <summary>
    /// Marker reading & parsing
    /// </summary>
    class jpeg_marker_reader
    {
        private const int APP0_DATA_LEN = 14;  /* Length of interesting data in APP0 */
        private const int APP14_DATA_LEN = 12;  /* Length of interesting data in APP14 */
        private const int APPN_DATA_LEN = 14;  /* Must be the largest of the above!! */

        private jpeg_decompress_struct m_cinfo;

        /* Application-overridable marker processing methods */
        private jpeg_decompress_struct.jpeg_marker_parser_method m_process_COM;
        private jpeg_decompress_struct.jpeg_marker_parser_method[] m_process_APPn = new jpeg_decompress_struct.jpeg_marker_parser_method[16];

        /* Limit on marker data length to save for each marker type */
        private uint m_length_limit_COM;
        private uint[] m_length_limit_APPn = new uint[16];

        private bool m_saw_SOI;       /* found SOI? */
        private bool m_saw_SOF;       /* found SOF? */
        private int m_next_restart_num;       /* next restart number expected (0-7) */
        private uint m_discarded_bytes;   /* # of bytes skipped looking for a marker */

        /* Status of COM/APPn marker saving */
        private jpeg_marker_struct m_cur_marker; /* NULL if not processing a marker */
        private uint m_bytes_read;        /* data bytes read so far in marker */
        /* Note: cur_marker is not linked into marker_list until it's all read. */

        /// <summary>
        /// Initialize the marker reader module.
        /// This is called only once, when the decompression object is created.
        /// </summary>
        public jpeg_marker_reader(jpeg_decompress_struct cinfo)
        {
            m_cinfo = cinfo;

            /* Initialize COM/APPn processing.
            * By default, we examine and then discard APP0 and APP14,
            * but simply discard COM and all other APPn.
            */
            m_process_COM = skip_variable;
            m_length_limit_COM = 0;

            for (int i = 0; i < 16; i++)
            {
                m_process_APPn[i] = skip_variable;
                m_length_limit_APPn[i] = 0;
            }

            m_process_APPn[0] = get_interesting_appn;
            m_process_APPn[14] = get_interesting_appn;

            /* Reset marker processing state */
            reset_marker_reader();
        }

        /// <summary>
        /// Reset marker processing state to begin a fresh datastream.
        /// </summary>
        public void reset_marker_reader()
        {
            m_cinfo->m_comp_info = NULL;        /* until allocated by get_sof */
            m_cinfo->m_input_scan_number = 0;       /* no SOS seen yet */
            m_cinfo->m_unread_marker = 0;       /* no pending marker */
            m_saw_SOI = false;        /* set internal state too */
            m_saw_SOF = false;
            m_discarded_bytes = 0;
            m_cur_marker = NULL;
        }

        /// <summary>
        /// Read markers until SOS or EOI.
        /// 
        /// Returns same codes as are defined for jpeg_consume_input:
        /// JPEG_SUSPENDED, JPEG_REACHED_SOS, or JPEG_REACHED_EOI.
        /// </summary>
        public ReadResult read_markers()
        {
            /* Outer loop repeats once for each marker. */
            for (; ; )
            {
                /* Collect the marker proper, unless we already did. */
                /* NB: first_marker() enforces the requirement that SOI appear first. */
                if (m_cinfo->m_unread_marker == 0)
                {
                    if (!m_cinfo->m_marker->m_saw_SOI)
                    {
                        if (!first_marker())
                            return JPEG_SUSPENDED;
                    }
                    else
                    {
                        if (!next_marker())
                            return JPEG_SUSPENDED;
                    }
                }

                /* At this point m_cinfo->unread_marker contains the marker code and the
                 * input point is just past the marker proper, but before any parameters.
                 * A suspension will cause us to return with this state still true.
                 */
                switch (m_cinfo->m_unread_marker)
                {
                    case M_SOI:
                        if (!get_soi())
                            return JPEG_SUSPENDED;
                        break;

                    case M_SOF0:
                    /* Baseline */
                    case M_SOF1:
                        /* Extended sequential, Huffman */
                        if (!get_sof(false))
                            return JPEG_SUSPENDED;
                        break;

                    case M_SOF2:
                        /* Progressive, Huffman */
                        if (!get_sof(true))
                            return JPEG_SUSPENDED;
                        break;

                    /* Currently unsupported SOFn types */
                    case M_SOF3:
                    /* Lossless, Huffman */
                    case M_SOF5:
                    /* Differential sequential, Huffman */
                    case M_SOF6:
                    /* Differential progressive, Huffman */
                    case M_SOF7:
                    /* Differential lossless, Huffman */
                    case M_SOF9:
                    /* Extended sequential, arithmetic */
                    case M_SOF10:
                    /* Progressive, arithmetic */
                    case M_JPG:
                    /* Reserved for JPEG extensions */
                    case M_SOF11:
                    /* Lossless, arithmetic */
                    case M_SOF13:
                    /* Differential sequential, arithmetic */
                    case M_SOF14:
                    /* Differential progressive, arithmetic */
                    case M_SOF15:
                        /* Differential lossless, arithmetic */
                        m_cinfo->ERREXIT1(JERR_SOF_UNSUPPORTED, m_cinfo->m_unread_marker);
                        break;

                    case M_SOS:
                        if (!get_sos())
                            return JPEG_SUSPENDED;
                        m_cinfo->m_unread_marker = 0;   /* processed the marker */
                        return JPEG_REACHED_SOS;

                    case M_EOI:
                        m_cinfo->TRACEMS(1, JTRC_EOI);
                        m_cinfo->m_unread_marker = 0;   /* processed the marker */
                        return JPEG_REACHED_EOI;

                    case M_DAC:
                        if (!skip_variable(m_cinfo))
                            return JPEG_SUSPENDED;
                        break;

                    case M_DHT:
                        if (!get_dht())
                            return JPEG_SUSPENDED;
                        break;

                    case M_DQT:
                        if (!get_dqt())
                            return JPEG_SUSPENDED;
                        break;

                    case M_DRI:
                        if (!get_dri())
                            return JPEG_SUSPENDED;
                        break;

                    case M_APP0:
                    case M_APP1:
                    case M_APP2:
                    case M_APP3:
                    case M_APP4:
                    case M_APP5:
                    case M_APP6:
                    case M_APP7:
                    case M_APP8:
                    case M_APP9:
                    case M_APP10:
                    case M_APP11:
                    case M_APP12:
                    case M_APP13:
                    case M_APP14:
                    case M_APP15:
                        if (!(*(m_cinfo->m_marker)->m_process_APPn[m_cinfo->m_unread_marker - (int)M_APP0])(m_cinfo))
                            return JPEG_SUSPENDED;
                        break;

                    case M_COM:
                        if (!(*(m_cinfo->m_marker)->m_process_COM)(m_cinfo))
                            return JPEG_SUSPENDED;
                        break;

                    case M_RST0:
                    /* these are all parameterless */
                    case M_RST1:
                    case M_RST2:
                    case M_RST3:
                    case M_RST4:
                    case M_RST5:
                    case M_RST6:
                    case M_RST7:
                    case M_TEM:
                        m_cinfo->TRACEMS1(1, JTRC_PARMLESS_MARKER, m_cinfo->m_unread_marker);
                        break;

                    case M_DNL:
                        /* Ignore DNL ... perhaps the wrong thing */
                        if (!skip_variable(m_cinfo))
                            return JPEG_SUSPENDED;
                        break;

                    default:
                        /* must be DHP, EXP, JPGn, or RESn */
                        /* For now, we treat the reserved markers as fatal errors since they are
                         * likely to be used to signal incompatible JPEG Part 3 extensions.
                         * Once the JPEG 3 version-number marker is well defined, this code
                         * ought to change!
                         */
                        m_cinfo->ERREXIT1(JERR_UNKNOWN_MARKER, m_cinfo->m_unread_marker);
                        break;
                }

                /* Successfully processed marker, so reset state variable */
                m_cinfo->m_unread_marker = 0;
            } /* end loop */
        }

        /// <summary>
        /// Read a restart marker, which is expected to appear next in the datastream;
        /// if the marker is not there, take appropriate recovery action.
        /// Returns false if suspension is required.
        /// 
        /// Made public for use by entropy decoder only
        /// 
        /// This is called by the entropy decoder after it has read an appropriate
        /// number of MCUs.  cinfo->unread_marker may be nonzero if the entropy decoder
        /// has already read a marker from the data source.  Under normal conditions
        /// cinfo->unread_marker will be reset to 0 before returning; if not reset,
        /// it holds a marker which the decoder will be unable to read past.
        /// </summary>
        public bool read_restart_marker()
        {
            /* Obtain a marker unless we already did. */
            /* Note that next_marker will complain if it skips any data. */
            if (m_cinfo->m_unread_marker == 0)
            {
                if (!next_marker())
                    return false;
            }

            if (m_cinfo->m_unread_marker == ((int)M_RST0 + m_cinfo->m_marker->m_next_restart_num))
            {
                /* Normal case --- swallow the marker and let entropy decoder continue */
                m_cinfo->TRACEMS1(3, JTRC_RST, m_cinfo->m_marker->m_next_restart_num);
                m_cinfo->m_unread_marker = 0;
            }
            else
            {
                /* Uh-oh, the restart markers have been messed up. */
                /* Let the data source manager determine how to resync. */
                if (!m_cinfo->m_src->resync_to_restart(m_cinfo, m_cinfo->m_marker->m_next_restart_num))
                    return false;
            }

            /* Update next-restart state */
            m_cinfo->m_marker->m_next_restart_num = (m_cinfo->m_marker->m_next_restart_num + 1) & 7;

            return true;
        }

        /// <summary>
        /// Find the next JPEG marker, save it in cinfo->unread_marker.
        /// Returns false if had to suspend before reaching a marker;
        /// in that case cinfo->unread_marker is unchanged.
        /// 
        /// Note that the result might not be a valid marker code,
        /// but it will never be 0 or FF.
        /// </summary>
        public bool next_marker()
        {
            int c;
            for (; ; )
            {
                if (!m_cinfo->m_src->GetByte(c))
                    return false;

                /* Skip any non-FF bytes.
                 * This may look a bit inefficient, but it will not occur in a valid file.
                 * We sync after each discarded byte so that a suspending data source
                 * can discard the byte from its buffer.
                 */
                while (c != 0xFF)
                {
                    m_cinfo->m_marker->m_discarded_bytes++;
                    if (!m_cinfo->m_src->GetByte(c))
                        return false;
                }

                /* This loop swallows any duplicate FF bytes.  Extra FFs are legal as
                 * pad bytes, so don't count them in discarded_bytes.  We assume there
                 * will not be so many consecutive FF bytes as to overflow a suspending
                 * data source's input buffer.
                 */
                do
                {
                    if (!m_cinfo->m_src->GetByte(c))
                        return false;
                }
                while (c == 0xFF);

                if (c != 0)
                {
                    /* found a valid marker, exit loop */
                    break;
                }

                /* Reach here if we found a stuffed-zero data sequence (FF/00).
                 * Discard it and loop back to try again.
                 */
                m_cinfo->m_marker->m_discarded_bytes += 2;
            }

            if (m_cinfo->m_marker->m_discarded_bytes != 0)
            {
                m_cinfo->WARNMS2(JWRN_EXTRANEOUS_DATA, m_cinfo->m_marker->m_discarded_bytes, c);
                m_cinfo->m_marker->m_discarded_bytes = 0;
            }

            m_cinfo->m_unread_marker = c;
            return true;
        }

        /// <summary>
        /// Install a special processing method for COM or APPn markers.
        /// </summary>
        public void jpeg_set_marker_processor(int marker_code, jpeg_decompress_struct.jpeg_marker_parser_method routine)
        {
            if (marker_code == (int)M_COM)
                m_process_COM = routine;
            else if (marker_code >= (int)M_APP0 && marker_code <= (int)M_APP15)
                m_process_APPn[marker_code - (int)M_APP0] = routine;
            else
                m_cinfo->ERREXIT1(JERR_UNKNOWN_MARKER, marker_code);
        }

        public void jpeg_save_markers(int marker_code, uint length_limit)
        {
            /* Choose processor routine to use.
             * APP0/APP14 have special requirements.
             */
            jpeg_marker_parser_method processor;
            if (length_limit)
            {
                processor = save_marker;
                /* If saving APP0/APP14, save at least enough for our internal use. */
                if (marker_code == (int)M_APP0 && length_limit < APP0_DATA_LEN)
                    length_limit = APP0_DATA_LEN;
                else if (marker_code == (int)M_APP14 && length_limit < APP14_DATA_LEN)
                    length_limit = APP14_DATA_LEN;
            }
            else
            {
                processor = skip_variable;
                /* If discarding APP0/APP14, use our regular on-the-fly processor. */
                if (marker_code == (int)M_APP0 || marker_code == (int)M_APP14)
                    processor = get_interesting_appn;
            }

            if (marker_code == (int)M_COM)
            {
                m_process_COM = processor;
                m_length_limit_COM = length_limit;
            }
            else if (marker_code >= (int)M_APP0 && marker_code <= (int)M_APP15)
            {
                m_process_APPn[marker_code - (int)M_APP0] = processor;
                m_length_limit_APPn[marker_code - (int)M_APP0] = length_limit;
            }
            else
                m_cinfo->ERREXIT1(JERR_UNKNOWN_MARKER, marker_code);
        }

        /* State of marker reader, applications
        * supplying COM or APPn handlers might like to know the state.
        */
        public bool SawSOI()
        {
            return m_saw_SOI;
        }

        public bool SawSOF()
        {
            return m_saw_SOF;
        }

        public int NextRestartNumber()
        {
            return m_next_restart_num;
        }

        public uint DiscardedByteCount()
        {
            return m_discarded_bytes;
        }

        public void SkipBytes(uint count)
        {
            m_discarded_bytes += count;
        }

        /// <summary>
        /// Save an APPn or COM marker into the marker list
        /// </summary>
        private static bool save_marker(jpeg_decompress_struct cinfo)
        {
            jpeg_marker_struct* cur_marker = cinfo->m_marker->m_cur_marker;
    
            JOCTET* data = NULL;
            INT32 length = 0;
            unsigned int bytes_read;
            unsigned int data_length;

            if (cur_marker == NULL)
            {
                /* begin reading a marker */
                if (!cinfo->m_src->GetTwoBytes(length))
                    return false;

                length -= 2;
                if (length >= 0)
                {
                    /* watch out for bogus length word */
                    /* figure out how much we want to save */
                    unsigned int limit;
                    if (cinfo->m_unread_marker == (int) M_COM)
                        limit = cinfo->m_marker->m_length_limit_COM;
                    else
                        limit = cinfo->m_marker->m_length_limit_APPn[cinfo->m_unread_marker - (int) M_APP0];

                    if ((unsigned int) length < limit)
                        limit = (unsigned int) length;
                    
                    /* allocate and initialize the marker item */
                    cur_marker = new jpeg_marker_struct();
                    cur_marker->data = new JOCTET[limit];
                    cur_marker->next = NULL;
                    cur_marker->marker = (UINT8) cinfo->m_unread_marker;
                    cur_marker->original_length = (unsigned int) length;
                    cur_marker->data_length = limit;
                    
                    /* data area is just beyond the jpeg_marker_struct */
                    data = cur_marker->data = (JOCTET *) (cur_marker + 1);
                    cinfo->m_marker->m_cur_marker = cur_marker;
                    cinfo->m_marker->m_bytes_read = 0;
                    bytes_read = 0;
                    data_length = limit;
                }
                else
                {
                    /* deal with bogus length word */
                    bytes_read = data_length = 0;
                    data = NULL;
                }
            }
            else
            {
                /* resume reading a marker */
                bytes_read = cinfo->m_marker->m_bytes_read;
                data_length = cur_marker->data_length;
                data = cur_marker->data + bytes_read;
            }

            while (bytes_read < data_length)
            {
                /* move the restart point to here */
                cinfo->m_marker->m_bytes_read = bytes_read;

                /* If there's not at least one byte in buffer, suspend */
                if (!cinfo->m_src->MakeByteAvailable())
                    return false;

                /* Copy bytes with reasonable rapidity */
                bytes_read += cinfo->m_src->GetBytes(data, data_length - bytes_read);
            }

            /* Done reading what we want to read */
            if (cur_marker != NULL)
            {
                /* will be NULL if bogus length word */
                /* Add new marker to end of list */
                if (cinfo->m_marker_list == NULL)
                {
                    cinfo->m_marker_list = cur_marker;
                }
                else
                {
                    jpeg_marker_struct* prev = cinfo->m_marker_list;
                    while (prev->next != NULL)
                        prev = prev->next;

                    prev->next = cur_marker;
                }

                /* Reset pointer & calc remaining data length */
                data = cur_marker->data;
                length = cur_marker->original_length - data_length;
            }

            /* Reset to initial state for next marker */
            cinfo->m_marker->m_cur_marker = NULL;

            /* Process the marker if interesting; else just make a generic trace msg */
            switch (cinfo->m_unread_marker)
            {
            case M_APP0:
                examine_app0(cinfo, data, data_length, length);
                break;
            case M_APP14:
                examine_app14(cinfo, data, data_length, length);
                break;
            default:
                cinfo->TRACEMS2(1, JTRC_MISC_MARKER, cinfo->m_unread_marker, (int) (data_length + length));
                break;
            }

            /* skip any remaining data -- could be lots */
            if (length > 0)
                cinfo->m_src->skip_input_data(length);

            return true;
        }

        /// <summary>
        /// Skip over an unknown or uninteresting variable-length marker
        /// </summary>
        private static bool skip_variable(jpeg_decompress_struct cinfo)
        {
            INT32 length;
            if (!cinfo->m_src->GetTwoBytes(length))
                return false;

            length -= 2;

            cinfo->TRACEMS2(1, JTRC_MISC_MARKER, cinfo->m_unread_marker, (int)length);

            if (length > 0)
                cinfo->m_src->skip_input_data(length);

            return true;
        }

        /// <summary>
        /// Process an APP0 or APP14 marker without saving it
        /// </summary>
        private static bool get_interesting_appn(jpeg_decompress_struct cinfo)
        {
            INT32 length;
            if (!cinfo->m_src->GetTwoBytes(length))
                return false;

            length -= 2;

            /* get the interesting part of the marker data */
            int numtoread = 0;
            if (length >= APPN_DATA_LEN)
                numtoread = APPN_DATA_LEN;
            else if (length > 0)
                numtoread = length;

            JOCTET b[APPN_DATA_LEN];
            for (int i = 0; i < numtoread; i++)
            {
                int temp = 0;
                if (!cinfo->m_src->GetByte(temp))
                    return false;

                b[i] = (JOCTET) temp;
            }

            length -= numtoread;

            /* process it */
            switch (cinfo->m_unread_marker)
            {
            case M_APP0:
                examine_app0(cinfo, (JOCTET *) b, numtoread, length);
                break;
            case M_APP14:
                examine_app14(cinfo, (JOCTET *) b, numtoread, length);
                break;
            default:
                /* can't get here unless jpeg_save_markers chooses wrong processor */
                cinfo->ERREXIT1(JERR_UNKNOWN_MARKER, cinfo->m_unread_marker);
                break;
            }

            /* skip any remaining data -- could be lots */
            if (length > 0)
                cinfo->m_src->skip_input_data(length);

            return true;
        }

        /*
         * Routines for processing APPn and COM markers.
         * These are either saved in memory or discarded, per application request.
         * APP0 and APP14 are specially checked to see if they are
         * JFIF and Adobe markers, respectively.
         */

        /// <summary>
        /// Examine first few bytes from an APP0.
        /// Take appropriate action if it is a JFIF marker.
        /// datalen is # of bytes at data[], remaining is length of rest of marker data.
        /// </summary>
        private static void examine_app0(jpeg_decompress_struct cinfo, byte[] data, uint datalen, int remaining)
        {
            INT32 totallen = (INT32)datalen + remaining;

            if (datalen >= APP0_DATA_LEN &&
                data[0] == 0x4A &&
                data[1] == 0x46 &&
                data[2] == 0x49 &&
                data[3] == 0x46 &&
                data[4] == 0)
            {
                /* Found JFIF APP0 marker: save info */
                cinfo->m_saw_JFIF_marker = true;
                cinfo->m_JFIF_major_version = data[5];
                cinfo->m_JFIF_minor_version = data[6];
                cinfo->m_density_unit = data[7];
                cinfo->m_X_density = (data[8] << 8) + data[9];
                cinfo->m_Y_density = (data[10] << 8) + data[11];

                /* Check version.
                 * Major version must be 1, anything else signals an incompatible change.
                 * (We used to treat this as an error, but now it's a nonfatal warning,
                 * because some bozo at Hijaak couldn't read the spec.)
                 * Minor version should be 0..2, but process anyway if newer.
                 */
                if (cinfo->m_JFIF_major_version != 1)
                    cinfo->WARNMS2(JWRN_JFIF_MAJOR, cinfo->m_JFIF_major_version, cinfo->m_JFIF_minor_version);

                /* Generate trace messages */
                cinfo->TRACEMS5(1, JTRC_JFIF, cinfo->m_JFIF_major_version, cinfo->m_JFIF_minor_version, cinfo->m_X_density,
                                cinfo->m_Y_density, cinfo->m_density_unit);

                /* Validate thumbnail dimensions and issue appropriate messages */
                if (data[12] | data[13])
                    cinfo->TRACEMS2(1, JTRC_JFIF_THUMBNAIL, data[12], data[13]);

                totallen -= APP0_DATA_LEN;
                if (totallen != ((INT32)data[12] * (INT32)data[13] * (INT32)3))
                    cinfo->TRACEMS1(1, JTRC_JFIF_BADTHUMBNAILSIZE, (int)totallen);
            }
            else if (datalen >= 6 && data[0] == 0x4A && data[1] == 0x46 && data[2] == 0x58 && data[3] == 0x58 && data[4] == 0)
            {
                /* Found JFIF "JFXX" extension APP0 marker */
                /* The library doesn't actually do anything with these,
                 * but we try to produce a helpful trace message.
                 */
                switch (data[5])
                {
                    case 0x10:
                        cinfo->TRACEMS1(1, JTRC_THUMB_JPEG, (int)totallen);
                        break;
                    case 0x11:
                        cinfo->TRACEMS1(1, JTRC_THUMB_PALETTE, (int)totallen);
                        break;
                    case 0x13:
                        cinfo->TRACEMS1(1, JTRC_THUMB_RGB, (int)totallen);
                        break;
                    default:
                        cinfo->TRACEMS2(1, JTRC_JFIF_EXTENSION, data[5], (int)totallen);
                        break;
                }
            }
            else
            {
                /* Start of APP0 does not match "JFIF" or "JFXX", or too short */
                cinfo->TRACEMS1(1, JTRC_APP0, (int)totallen);
            }
        }

        /// <summary>
        /// Examine first few bytes from an APP14.
        /// Take appropriate action if it is an Adobe marker.
        /// datalen is # of bytes at data[], remaining is length of rest of marker data.
        /// </summary>
        private static void examine_app14(jpeg_decompress_struct cinfo, byte[] data, uint datalen, int remaining)
        {
            if (datalen >= APP14_DATA_LEN &&
                data[0] == 0x41 &&
                data[1] == 0x64 &&
                data[2] == 0x6F &&
                data[3] == 0x62 &&
                data[4] == 0x65)
            {
                /* Found Adobe APP14 marker */
                unsigned int version = (data[5] << 8) + data[6];
                unsigned int flags0 = (data[7] << 8) + data[8];
                unsigned int flags1 = (data[9] << 8) + data[10];
                unsigned int transform = data[11];
                cinfo->TRACEMS4(1, JTRC_ADOBE, version, flags0, flags1, transform);
                cinfo->m_saw_Adobe_marker = true;
                cinfo->m_Adobe_transform = (UINT8) transform;
            }
            else
            {
                /* Start of APP14 does not match "Adobe", or too short */
                cinfo->TRACEMS1(1, JTRC_APP14, (int) (datalen + remaining));
            }
        }

        /*
         * Routines to process JPEG markers.
         *
         * Entry condition: JPEG marker itself has been read and its code saved
         *   in cinfo->unread_marker; input restart point is just after the marker.
         *
         * Exit: if return true, have read and processed any parameters, and have
         *   updated the restart point to point after the parameters.
         *   If return false, was forced to suspend before reaching end of
         *   marker parameters; restart point has not been moved.  Same routine
         *   will be called again after application supplies more input data.
         *
         * This approach to suspension assumes that all of a marker's parameters
         * can fit into a single input bufferload.  This should hold for "normal"
         * markers.  Some COM/APPn markers might have large parameter segments
         * that might not fit.  If we are simply dropping such a marker, we use
         * skip_input_data to get past it, and thereby put the problem on the
         * source manager's shoulders.  If we are saving the marker's contents
         * into memory, we use a slightly different convention: when forced to
         * suspend, the marker processor updates the restart point to the end of
         * what it's consumed (ie, the end of the buffer) before returning false.
         * On resumption, cinfo->unread_marker still contains the marker code,
         * but the data source will point to the next chunk of marker data.
         * The marker processor must retain internal state to deal with this.
         *
         * Note that we don't bother to avoid duplicate trace messages if a
         * suspension occurs within marker parameters.  Other side effects
         * require more care.
         */


        /// <summary>
        /// Process an SOI marker
        /// </summary>
        private bool get_soi()
        {
            m_cinfo->TRACEMS(1, JTRC_SOI);

            if (m_cinfo->m_marker->m_saw_SOI)
                m_cinfo->ERREXIT(JERR_SOI_DUPLICATE);

            /* Reset all parameters that are defined to be reset by SOI */
            m_cinfo->m_restart_interval = 0;

            /* Set initial assumptions for colorspace etc */

            m_cinfo->m_jpeg_color_space = JCS_UNKNOWN;
            m_cinfo->m_CCIR601_sampling = false; /* Assume non-CCIR sampling??? */

            m_cinfo->m_saw_JFIF_marker = false;
            m_cinfo->m_JFIF_major_version = 1; /* set default JFIF APP0 values */
            m_cinfo->m_JFIF_minor_version = 1;
            m_cinfo->m_density_unit = 0;
            m_cinfo->m_X_density = 1;
            m_cinfo->m_Y_density = 1;
            m_cinfo->m_saw_Adobe_marker = false;
            m_cinfo->m_Adobe_transform = 0;

            m_cinfo->m_marker->m_saw_SOI = true;

            return true;
        }

        /// <summary>
        /// Process a SOFn marker
        /// </summary>
        private bool get_sof(bool is_prog)
        {
            m_cinfo->m_progressive_mode = is_prog;

            INT32 length;
            if (!m_cinfo->m_src->GetTwoBytes(length))
                return false;

            if (!m_cinfo->m_src->GetByte(m_cinfo->m_data_precision))
                return false;

            INT32 temp = 0;
            if (!m_cinfo->m_src->GetTwoBytes(temp))
                return false;
            m_cinfo->m_image_height = temp;

            if (!m_cinfo->m_src->GetTwoBytes(temp))
                return false;
            m_cinfo->m_image_width = temp;

            if (!m_cinfo->m_src->GetByte(m_cinfo->m_num_components))
                return false;

            length -= 8;

            m_cinfo->TRACEMS4(1, JTRC_SOF, m_cinfo->m_unread_marker, (int)m_cinfo->m_image_width, (int)m_cinfo->m_image_height,
                              m_cinfo->m_num_components);

            if (m_cinfo->m_marker->m_saw_SOF)
                m_cinfo->ERREXIT(JERR_SOF_DUPLICATE);

            /* We don't support files in which the image height is initially specified */
            /* as 0 and is later redefined by DNL.  As long as we have to check that,  */
            /* might as well have a general sanity check. */
            if (m_cinfo->m_image_height <= 0 || m_cinfo->m_image_width <= 0 || m_cinfo->m_num_components <= 0)
                m_cinfo->ERREXIT(JERR_EMPTY_IMAGE);

            if (length != (m_cinfo->m_num_components * 3))
                m_cinfo->ERREXIT(JERR_BAD_LENGTH);

            if (m_cinfo->m_comp_info == NULL)
            {
                /* do only once, even if suspend */
                m_cinfo->m_comp_info = new jpeg_component_info[m_cinfo->m_num_components];
            }

            for (int ci = 0; ci < m_cinfo->m_num_components; ci++)
            {
                m_cinfo->m_comp_info[ci].component_index = ci;
                if (!m_cinfo->m_src->GetByte(m_cinfo->m_comp_info[ci].component_id))
                    return false;

                int c;
                if (!m_cinfo->m_src->GetByte(c))
                    return false;

                m_cinfo->m_comp_info[ci].h_samp_factor = (c >> 4) & 15;
                m_cinfo->m_comp_info[ci].v_samp_factor = (c) & 15;
                if (!m_cinfo->m_src->GetByte(m_cinfo->m_comp_info[ci].quant_tbl_no))
                    return false;

                m_cinfo->TRACEMS4(1, JTRC_SOF_COMPONENT, m_cinfo->m_comp_info[ci].component_id,
                    m_cinfo->m_comp_info[ci].h_samp_factor, m_cinfo->m_comp_info[ci].v_samp_factor,
                    m_cinfo->m_comp_info[ci].quant_tbl_no);
            }

            m_cinfo->m_marker->m_saw_SOF = true;
            return true;
        }

        /// <summary>
        /// Process a SOS marker
        /// </summary>
        private bool get_sos()
        {
            if (!m_cinfo->m_marker->m_saw_SOF)
                m_cinfo->ERREXIT(JERR_SOS_NO_SOF);

            INT32 length;
            if (!m_cinfo->m_src->GetTwoBytes(length))
                return false;

            /* Number of components */
            int n;
            if (!m_cinfo->m_src->GetByte(n))
                return false;

            m_cinfo->TRACEMS1(1, JTRC_SOS, n);

            if (length != (n * 2 + 6) || n < 1 || n > MAX_COMPS_IN_SCAN)
                m_cinfo->ERREXIT(JERR_BAD_LENGTH);

            m_cinfo->m_comps_in_scan = n;

            /* Collect the component-spec parameters */

            for (int i = 0; i < n; i++)
            {
                int cc;
                if (!m_cinfo->m_src->GetByte(cc))
                    return false;

                int c;
                if (!m_cinfo->m_src->GetByte(c))
                    return false;

                bool idFound = false;
                int foundIndex = -1;
                for (int ci = 0; ci < m_cinfo->m_num_components; ci++)
                {
                    if (cc == m_cinfo->m_comp_info[ci].component_id)
                    {
                        foundIndex = ci;
                        idFound = true;
                        break;
                    }
                }

                if (!idFound)
                    m_cinfo->ERREXIT1(JERR_BAD_COMPONENT_ID, cc);

                m_cinfo->m_cur_comp_info[i] = &m_cinfo->m_comp_info[foundIndex];
                m_cinfo->m_comp_info[foundIndex].dc_tbl_no = (c >> 4) & 15;
                m_cinfo->m_comp_info[foundIndex].ac_tbl_no = (c) & 15;

                m_cinfo->TRACEMS3(1, JTRC_SOS_COMPONENT, cc,
                    m_cinfo->m_comp_info[foundIndex].dc_tbl_no, m_cinfo->m_comp_info[foundIndex].ac_tbl_no);
            }

            /* Collect the additional scan parameters Ss, Se, Ah/Al. */
            int c;
            if (!m_cinfo->m_src->GetByte(c))
                return false;

            m_cinfo->m_Ss = c;
            if (!m_cinfo->m_src->GetByte(c))
                return false;

            m_cinfo->m_Se = c;
            if (!m_cinfo->m_src->GetByte(c))
                return false;

            m_cinfo->m_Ah = (c >> 4) & 15;
            m_cinfo->m_Al = (c) & 15;

            m_cinfo->TRACEMS4(1, JTRC_SOS_PARAMS, m_cinfo->m_Ss, m_cinfo->m_Se, m_cinfo->m_Ah, m_cinfo->m_Al);

            /* Prepare to scan data & restart markers */
            m_cinfo->m_marker->m_next_restart_num = 0;

            /* Count another SOS marker */
            m_cinfo->m_input_scan_number++;
            return true;
        }

        /// <summary>
        /// Process a DHT marker
        /// </summary>
        private bool get_dht()
        {
            INT32 length;
            if (!m_cinfo->m_src->GetTwoBytes(length))
                return false;

            length -= 2;

            UINT8 bits[17];
            UINT8 huffval[256];
            while (length > 16)
            {
                int index;
                if (!m_cinfo->m_src->GetByte(index))
                    return false;

                m_cinfo->TRACEMS1(1, JTRC_DHT, index);

                bits[0] = 0;
                int count = 0;
                for (int i = 1; i <= 16; i++)
                {
                    int temp = 0;
                    if (!m_cinfo->m_src->GetByte(temp))
                        return false;

                    bits[i] = (UINT8) temp;
                    count += bits[i];
                }

                length -= 1 + 16;

                m_cinfo->TRACEMS8(2, JTRC_HUFFBITS, bits[1], bits[2], bits[3], bits[4], bits[5], bits[6], bits[7], bits[8]);
                m_cinfo->TRACEMS8(2, JTRC_HUFFBITS, bits[9], bits[10], bits[11], bits[12], bits[13], bits[14], bits[15], bits[16]);

                /* Here we just do minimal validation of the counts to avoid walking
                 * off the end of our table space.  jdhuff.c will check more carefully.
                 */
                if (count > 256 || ((INT32) count) > length)
                    m_cinfo->ERREXIT(JERR_BAD_HUFF_TABLE);

                for (int i = 0; i < count; i++)
                {
                    int temp = 0;
                    if (!m_cinfo->m_src->GetByte(temp))
                        return false;

                    huffval[i] = (UINT8) temp;
                }

                length -= count;

                JHUFF_TBL* htblptr = NULL;
                if (index & 0x10)
                {
                    /* AC table definition */
                    index -= 0x10;
                    if (m_cinfo->m_ac_huff_tbl_ptrs[index] == NULL)
                        m_cinfo->m_ac_huff_tbl_ptrs[index] = new JHUFF_TBL();

                    htblptr = m_cinfo->m_ac_huff_tbl_ptrs[index];
                }
                else
                {
                    /* DC table definition */
                    if (m_cinfo->m_dc_huff_tbl_ptrs[index] == NULL)
                        m_cinfo->m_dc_huff_tbl_ptrs[index] = new JHUFF_TBL();

                    htblptr = m_cinfo->m_dc_huff_tbl_ptrs[index];
                }

                if (index < 0 || index >= NUM_HUFF_TBLS)
                    m_cinfo->ERREXIT1(JERR_DHT_INDEX, index);

                memcpy((void *) htblptr->bits, (const void *) bits, sizeof(htblptr->bits));
                memcpy((void *) htblptr->huffval, (const void *) huffval, sizeof(htblptr->huffval));
            }

            if (length != 0)
                m_cinfo->ERREXIT(JERR_BAD_LENGTH);

            return true;
        }

        /// <summary>
        /// Process a DQT marker
        /// </summary>
        private bool get_dqt()
        {
            INT32 length;
            if (!m_cinfo->m_src->GetTwoBytes(length))
                return false;

            length -= 2;
            while (length > 0)
            {
                int n;
                if (!m_cinfo->m_src->GetByte(n))
                    return false;

                int prec = n >> 4;
                n &= 0x0F;

                m_cinfo->TRACEMS2(1, JTRC_DQT, n, prec);

                if (n >= NUM_QUANT_TBLS)
                    m_cinfo->ERREXIT1(JERR_DQT_INDEX, n);

                if (m_cinfo->m_quant_tbl_ptrs[n] == NULL)
                    m_cinfo->m_quant_tbl_ptrs[n] = new JQUANT_TBL();

                JQUANT_TBL* quant_ptr = m_cinfo->m_quant_tbl_ptrs[n];

                for (int i = 0; i < DCTSIZE2; i++)
                {
                    unsigned int tmp;
                    if (prec)
                    {
                        INT32 temp = 0;
                        if (!m_cinfo->m_src->GetTwoBytes(temp))
                            return false;

                        tmp = temp;
                    }
                    else
                    {
                        int temp = 0;
                        if (!m_cinfo->m_src->GetByte(temp))
                            return false;

                        tmp = temp;
                    }

                    /* We convert the zigzag-order table to natural array order. */
                    quant_ptr->quantval[JpegUtils::jpeg_natural_order[i]] = (UINT16) tmp;
                }

                if (m_cinfo->m_err->m_trace_level >= 2)
                {
                    for (int i = 0; i < DCTSIZE2; i += 8)
                    {
                        m_cinfo->TRACEMS8(2, JTRC_QUANTVALS, quant_ptr->quantval[i], 
                            quant_ptr->quantval[i + 1], quant_ptr->quantval[i + 2], 
                            quant_ptr->quantval[i + 3], quant_ptr->quantval[i + 4],
                            quant_ptr->quantval[i + 5], quant_ptr->quantval[i + 6], quant_ptr->quantval[i + 7]);
                    }
                }

                length -= DCTSIZE2 + 1;
                if (prec)
                    length -= DCTSIZE2;
            }

            if (length != 0)
                m_cinfo->ERREXIT(JERR_BAD_LENGTH);

            return true;
        }

        /// <summary>
        /// Process a DRI marker
        /// </summary>
        private bool get_dri()
        {
            INT32 length;
            if (!m_cinfo->m_src->GetTwoBytes(length))
                return false;

            if (length != 4)
                m_cinfo->ERREXIT(JERR_BAD_LENGTH);

            INT32 temp = 0;
            if (!m_cinfo->m_src->GetTwoBytes(temp))
                return false;
            
            unsigned int tmp = temp;
            m_cinfo->TRACEMS1(1, JTRC_DRI, tmp);
            m_cinfo->m_restart_interval = tmp;

            return true;
        }

        /// <summary>
        /// Like next_marker, but used to obtain the initial SOI marker.
        /// For this marker, we do not allow preceding garbage or fill; otherwise,
        /// we might well scan an entire input file before realizing it ain't JPEG.
        /// If an application wants to process non-JFIF files, it must seek to the
        /// SOI before calling the JPEG library.
        /// </summary>
        private bool first_marker()
        {
            int c;
            if (!m_cinfo->m_src->GetByte(c))
                return false;

            int c2;
            if (!m_cinfo->m_src->GetByte(c2))
                return false;

            if (c != 0xFF || c2 != (int)M_SOI)
                m_cinfo->ERREXIT2(JERR_NO_SOI, c, c2);

            m_cinfo->m_unread_marker = c2;
            return true;
        }
    }
}