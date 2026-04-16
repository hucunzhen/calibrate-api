#ifndef	ROBOTBASE_H
#define	ROBOTBASE_H

#ifdef ALGORITHM_EXPORTS
#define DLLEXPORT_API extern "C" _declspec(dllexport)
#else
#define DLLEXPORT_API extern "C" _declspec(dllimport)
#endif

#include "XVBase.h"
#include "ContourPos.h"
#include <vector>
using namespace std;

namespace XVL
{
	typedef struct CameraParams
	{
		float A[3][3];  //相机内参矩阵[fx,skew,cx; 0,fy,cy; 0,0,1]
		float k1;       //RadialDistortion系数
		float k2;
		float p1;       //TangentialDistortion系数
		float p2;
	}CameraParams;

	typedef struct RobotParams   //机械手参数
	{
		float firstArmLength;    //scara第一臂长
		float secondArmLength;   //scara第二臂长
	}RobotParams;

	enum XYZCameraAssembleType //XYZ平台相机架设方式
	{
		UpCamera,              //静态相机 向上拍摄
		DownCamera,            //静态相机 向下拍摄
		other,                 //其他
	};

	//typedef struct RobotFlangePose //手臂法兰姿态
	//{
	//	float                                           x; //单位mm
	//	float                                           y;
	//	float                                           z;

	//	float                                           U; //J1+J2+J4
	//	float                                           V;
	//	float                                           W;
	//	float                                           J1; //臂1旋转角度
	//	float                                           J2; //臂1旋转角度
	//	float                                           J3; //d3单位mm
	//	float                                           J4; //法兰自传角度
	//}RobotFlangePose;

	class RobotFlangePose //手臂法兰姿态  
	{
	public:
		RobotFlangePose(float x = 1000.0, float y = 1000.0, float z = 1000.0, float U = 1000.0, float V = 0, float W = 0, float J1 = 0, float J2 = 0, float J3 = 0, float J4 = 0)
		{
			this->x = x; // 使用 this 指针来初始化成员变量
			this->y = y;
			this->z = z;
			this->U = U;
			this->V = V;
			this->W = W;
			this->J1 = J1;
			this->J2 = J2;
			this->J3 = J3;
			this->J4 = J4;
		}
		float                                           x; //单位mm
		float                                           y;
		float                                           z;
		float                                           U; //J1+J2+J4
		float                                           V;
		float                                           W;
		float                                           J1; //臂1旋转角度
		float                                           J2; //臂1旋转角度
		float                                           J3; //d3单位mm
		float                                           J4; //法兰自传角度
	};

	class CompensationPose //补偿姿态
	{
	public:
		CompensationPose(float x = 0, float y = 0, float U = 0)
		{
			this->x = x;
			this->y = y;
			this->U = U;
		}
		float                                           x; //单位毫米mm
		float                                           y;
		float                                           U; //单位度°
	};
	//typedef struct CompensationPose //补偿姿态
	//{
	//	float                                           x; //单位毫米mm
	//	float                                           y;
	//	float                                           U; //单位度°
	//}CompensationPose;

	typedef struct RobotToolPose //手臂工具姿态
	{
		float                                           R[3][3];
		float                                           T[3];
	}RobotToolPose;

	typedef struct ImagePointFloat //图像坐标点 原点是左上(0,0)
	{
		float                                           x;
		float                                           y;
	}ImagePointFloat;

	typedef struct RobotPointFloat //手臂基坐标系下的坐标
	{
		float                                           x;
		float                                           y;
		float                                           z;
	}RobotPointFloat;

	///////工具标定
	class  GetCalirbateMarkPointIn //圆形Mark点获取
	{
	public:
		XVImage*              inImage;        //输入图像
		XVContourRectangle2D  detectRegion;   //检测区域
		int                   markPointDetectGradientThreshold; //梯度阈值20
		int                   markPointDetectMinChainLength;    //最小链长200
		int                   markPointDetectMinRadius;         //最小半径2
		int                   markPointDetectMaxRadius;         //最大半径50
		CalculateParam        calculateParam;
		GetCalirbateMarkPointIn() //简单构造
		{
			//this->markPointDetectMinChainLength = 200;
			//this->markPointDetectMaxRadius = 50;
		}
		~GetCalirbateMarkPointIn()
		{
			if (this->calculateParam.AngleTable != 0)
			{
				free(this->calculateParam.AngleTable);
				free(this->calculateParam.GradientTable);
			}
		}
	};
	DLLEXPORT_API void XVInitGetCalirbateMarkPointIn(GetCalirbateMarkPointIn *calculateParam);//初始化

	typedef struct GetCalirbateMarkPointOut
	{
		int             getCalirbateMarkPointResult;
		XVCircle2D      circles;
		ChainObj chainObj;
	}GetCalirbateMarkPointOut;
	DLLEXPORT_API void XVGetCalculateParam(CalculateParam *calculateParam);
	DLLEXPORT_API void XVGetCalirbateMarkPoint(GetCalirbateMarkPointIn *getCalirbateMarkPointIn, GetCalirbateMarkPointOut *getCalirbateMarkPointOut);

	enum XVMarkPointType //mark点类型
	{
		circle,        //圆
		circular_ring, //圆环
	};
	typedef struct ToolCaliGetRobotPoseIn //工具标定手臂姿态获取输入
	{
		XVMarkPointType markPointType;    //df:0
		float detectAccuracy;             //检测精度(中心点之间的距离) df:0.2pixel
	}ToolCaliGetRobotPoseIn;
	typedef struct ToolCaliGetRobotPoseOut //工具标定手臂姿态获取输出
	{
		int                     ToolCaliGetRobotPoseResult;
		XVCircle2D              modelCircle;        //基准点红色
		XVCircle2D              objectCircleBlue;   //蓝色目标点
		XVCircle2D              objectCircleGreen;  //绿色目标点
		float                   centerDistance;
	}ToolCaliGetRobotPoseOut;
	typedef struct RobotToolCalibrateIn //工具标定输入
	{
		vector<RobotFlangePose> robotFlangePose;
	}RobotToolCalibrateIn;
	typedef struct RobotToolCalibrateOut //工具标定输出
	{
		int                     robotToolCalibrateResult;
		RobotToolPose           robotToolPose;
		float                   pointResidual_maxOut; //最大标定误差
		float                   pointResidual_avOut;  //平均标定误差
		float                   markPointInBaseX;     //mark点在基坐标系下的估计(运动相机标定时用到)
		float                   markPointInBaseY;
	}RobotToolCalibrateOut;

	DLLEXPORT_API void XVToolCaliGetRobotPose(ToolCaliGetRobotPoseIn *toolCaliGetRobotPoseIn, ContourPosLearnOut *contourPosLearn_Out, ContourPosMatchOut *contourPosMatch_Out, ToolCaliGetRobotPoseOut *toolCaliGetRobotPoseOut);
	DLLEXPORT_API void XVToolCaliGetRobotPose2(ToolCaliGetRobotPoseIn *toolCaliGetRobotPoseIn, GetCalirbateMarkPointOut *GetCalirbateMarkPointOut_Learn, GetCalirbateMarkPointOut *GetCalirbateMarkPointOut_Match, ToolCaliGetRobotPoseOut *toolCaliGetRobotPoseOut);
	DLLEXPORT_API void XVRobotToolCalibrate(RobotToolCalibrateIn *robotToolCalibrateIn, RobotToolCalibrateOut *robotToolCalibrateOut);//工具标定

	typedef struct GetRobotToolPointIn  //获取工具末端点坐标
	{
		RobotToolCalibrateOut *robotToolCalibrateOut;
		RobotFlangePose robotFlangePose;//手臂当前姿态
	}GetRobotToolPointIn;
	typedef struct GetRobotToolPointOut
	{
		int             getRobotToolPointResult;
		RobotPointFloat robotPointFloat;
	}GetRobotToolPointOut;
	DLLEXPORT_API void XVGetRobotToolPoint(GetRobotToolPointIn *getRobotToolPointIn, GetRobotToolPointOut *getRobotToolPointOut);


	typedef struct GetCircleCenterContourIn //轮廓定位圆心提取
	{
		ContourPosMatchOut      *contourPosMatch_Out;
	}GetCircleCenterContourIn;
	typedef struct GetCircleCenterContourOut //工具标定输出
	{
		vector<XVCircle2D>      circles;
	}GetCircleCenterContourOut;
	DLLEXPORT_API void XVGetCircleCenterContour(GetCircleCenterContourIn *getCircleCenterContourIn, GetCircleCenterContourOut *getCircleCenterContourOut);
	////

}
#endif