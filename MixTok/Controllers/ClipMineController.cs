﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MixTok.Core;

namespace MixTok.Controllers
{
    [Route("api/v1/[controller]")]
    [ApiController]
    public class ClipMineController : ControllerBase
    {
        // GET: api/v1/ClipMine
        [HttpGet]
        public async Task<IActionResult> Get(int sortType, int? limit, string fromTime, string toTime, int? viewCoutMin, int? channelId, string channelName, bool? currentlyLive, bool partnered, string gameTitle, int? gameId, int? hypeZoneChannelId)
        {
            DateTime start = DateTime.Now;

            // Setup the limit and page values.
            int l = limit.HasValue ? limit.Value : 100;
            l = Math.Clamp(l, 0, 1000);

            // If we get a channel name, convert it to a channel id.
            if(!string.IsNullOrWhiteSpace(channelName))
            {
                channelId = await MixerApis.GetChannelId(channelName);
                if(channelId == 0)
                {
                    return BadRequest("Invalid Channel Name");
                }
            }

            // If we get time ranges, parse them.
            DateTime? from = null;
            DateTime? to = null;
            if (!String.IsNullOrWhiteSpace(fromTime))
            {
                from = ParseTime(fromTime);
                if(from == null)
                {
                    return BadRequest("Invalid time format. We accept unix time, anything C# DateTime can parse, or formats like `1d`, `5h`, etc.");
                }
            }
            if (!String.IsNullOrWhiteSpace(toTime))
            {
                from = ParseTime(toTime);
                if (from == null)
                {
                    return BadRequest("Invalid time format. We accept unix time, anything C# DateTime can parse, or formats like `1d`, `5h`, etc.");
                }
            }

            // Figure out the sort
            ClipMineSortTypes sort = ClipMineSortTypes.ViewCount;
            switch (sortType)
            {
                case 0:
                    sort = ClipMineSortTypes.ViewCount;
                    break;
                case 1:
                    sort = ClipMineSortTypes.MixTokRank;
                    break;
                default:
                    return BadRequest("Invalid sort type, ViewCount=0, MixTockRank=1");
            }

            // Get the results.
            var list = Program.s_ClipMine.GetClips(sort, l, from, to, viewCoutMin, channelId, hypeZoneChannelId, currentlyLive, partnered, gameTitle, gameId);
            Logger.Info($"ClipMine call took {DateTime.Now - start}");
            return Ok(list);
        }  
        
        public DateTime? ParseTime(string inputTime)
        {
            if(String.IsNullOrWhiteSpace(inputTime))
            {
                return null;
            }

            long unixTime = 0;
            DateTime parsedTime;

            // Look for anything that datetime can parse.
            if (DateTime.TryParse(inputTime, out parsedTime))
            {
                return parsedTime;
            }
            // Look for word style, ex `1d`, `5h`.
            else if(Regex.IsMatch(inputTime, @"^[-]?[0-9]+[a-zA-Z]"))
            {
                // Remove any starting -
                if(inputTime.StartsWith('-'))
                {
                    inputTime = inputTime.Substring(1);
                }

                // find the number values
                int count = 0;
                string num = "";
                while(count < inputTime.Length)
                {
                    if(!char.IsNumber(inputTime[count]))
                    {
                        break;
                    }
                    num += inputTime[count];
                    count++;
                }
                if(String.IsNullOrWhiteSpace(num))
                {
                    return null;
                }
                int value = int.Parse(num);

                // Find the letter
                if(count >= inputTime.Length)
                {
                    return null;
                }
                char type = inputTime[count];

                DateTime now = DateTime.UtcNow;
                switch(type)
                {
                    case 's':
                        return now.AddSeconds(-value);
                    case 'm':
                        return now.AddMinutes(-value);
                    case 'h':
                        return now.AddHours(-value);
                    case 'd':
                        return now.AddDays(-value);
                    default:
                        return null;
                }
            }
            // Look for a unix timestamp
            else if (long.TryParse(inputTime, out unixTime))
            {
                return new DateTime(1970, 1, 1, 0, 0, 0, 0).ToUniversalTime().AddSeconds(unixTime);
            }
            return null;
        }
    }
}