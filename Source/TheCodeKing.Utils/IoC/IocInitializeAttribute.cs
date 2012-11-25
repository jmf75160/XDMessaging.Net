﻿/*=============================================================================
*
*	(C) Copyright 2011, Michael Carlisle (mike.carlisle@thecodeking.co.uk)
*
*   http://www.TheCodeKing.co.uk
*  
*	All rights reserved.
*	The code and information is provided "as-is" without waranty of any kind,
*	either expressed or implied.
*
*=============================================================================
*/
using System;
using TheCodeKing.Utils.Contract;

namespace TheCodeKing.Utils.IoC
{
    [AttributeUsage(AttributeTargets.Class)]
    public class IocInitializeAttribute : Attribute
    {
        private readonly Type registerType;

        #region Constructors and Destructors

        public IocInitializeAttribute()
        {
        }

        public IocInitializeAttribute(string name, Type registerType=null)
        {
            this.registerType = registerType;
            Validate.That(name).IsNotNullOrEmpty();

            Name = name;
        }

        #endregion

        #region Properties

        public Type RegisterType
        {
            get { return registerType; }
        }

        public string Name { get; set; }

        #endregion
    }
}