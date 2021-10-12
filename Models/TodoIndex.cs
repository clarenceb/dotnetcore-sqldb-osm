using System;
using System.ComponentModel.DataAnnotations;
using DotNetCoreSqlDb.Models;
using System.Collections.Generic;

namespace DotNetCoreSqlDb.Models
{
    public class TodoIndex
    {
        public IEnumerable<Todo> Todos { get; set; }

        public String TimeOfDay { get; set; }
    }
}