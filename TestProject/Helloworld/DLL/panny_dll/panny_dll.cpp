// Dll1.cpp : 定义 DLL 应用程序的导出函数。
//

#include "stdafx.h"
#include "panny_dll.h"
int panny(int i, int(*call_back)(int a, int b))
{
	int aa;
	aa = i * i;
	call_back(i, aa);
	return 0;
}
